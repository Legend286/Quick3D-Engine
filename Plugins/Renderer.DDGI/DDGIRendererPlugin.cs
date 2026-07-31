// SPDX-License-Identifier: MIT
using Engine.CBindings;
using Engine.Plugins;
using Engine.RenderGraph;
using Engine.Scene;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;

namespace Engine.DDGI;

/// <summary>
/// DDGI renderer plugin. Phase-3 wiring: hosts the Phase-1 CPU
/// primitives (<see cref="DDGIProbeVolume"/>, <see cref="DDGIProbePriority"/>),
/// allocates the GPU atlas bundle (<see cref="DDGIAtlasResources"/>) on
/// first <see cref="BuildPlan"/> call, exposes it through the
/// renderer-free <see cref="IDDGIAtlasProvider"/> contract so the
/// canonical clustered plan can wire atlas bindings into the PBR
/// pass without naming Engine.DDGI types.
///
/// On toggle-off / plugin reload the
/// <see cref="DDGIAtlasProviderRegistry.Unregister"/> call drops the
/// provider so subsequent PBR frames degrade to no-atlas sampling.
/// </summary>
public sealed class DDGIRendererPlugin :
    IEnginePlugin,
    IRendererPlanPlugin,
    IRendererPlanPluginLite,
    IDDGIAtlasProvider
{
    /// <inheritdoc />
    public string Id => "renderer.ddgi";

    private const int LogSampleEvery = 60;

    // MARK: Fingerprint-stability counters for the disappearance
    // audit. The fingerprint is currently (modelCount, lightCount,
    // contentHash); if modelCount or lightCount churn per frame
    // (ECS streaming, async entity creation), every churn resets
    // the sparse layout, so the placement must run again next tick
    // and the debug viz hides for >=1 frame. Cumulative counters
    // surface the pattern without flooding every tick.
    private int _fingerprintResetsTotal;
    private long _fingerprintResetsWindowStartTick;


    private readonly DDGIProbeVolume _volume;
    private readonly DDGIProbePriority.Tuning _tuning;
    private readonly DDGIProbePriority _priority;
    private readonly DDGILightTreeBuilder _lightTreeBuilder = new();
    private DDGIProbePriority.ProbeSnapshot[] _probeSnapshots =
        Array.Empty<DDGIProbePriority.ProbeSnapshot>();
    private DDGILightSnapshot[] _currentLightSnapshot =
        Array.Empty<DDGILightSnapshot>();
    private bool _lightsSeeded;
    private long _tickCounter;
    private long _lastPlacementTick = -1;
    private long _sceneFingerprint;
    private DDGIProbePriority.LightInfluence[] _lastInfluences =
        Array.Empty<DDGIProbePriority.LightInfluence>();
    private SceneGraph? _currentScene;

    public DDGIRendererPlugin()
    {
        _volume = new DDGIProbeVolume(
            new Vector3(0f, 0f, 0f),
            new Vector3(32f, 16f, 32f),
            gridResolution: DDGIProbeVolume.DefaultBaseGridResolution,
            maxProbesTotalBudget:
                DDGIProbeVolume.DefaultMaxProbesTotalBudget);

        _tuning = new DDGIProbePriority.Tuning(
            DistanceWeight: 1.0f,
            DistanceFalloffMeters: 24f,
            FrustumContainmentBonus: 0.5f,
            StalePenaltyPerFrame: 0.05f,
            StalePenaltyCap: 1.0f,
            DirtyLightBoost: 4.0f,
            DirtyLightBaseBoost: 0.5f);

        _priority = new DDGIProbePriority(_tuning);
        _lightsSeeded = false;
        _tickCounter = 0;
    }

    private DDGIAtlasResources? _atlas;
    private bool _atlasAllocFailed;
    private IEnginePluginHost? _host;

    // MARK: Plugin-lifetime CancellationToken. Instantiated in
    // Initialize() and disposed in Shutdown() so a fresh CTS is paired
    // with each plugin (re)activation. Pass _lifecycleCts.Token into
    // ConstructPassWithTimeout so the wrapping factory can pre-check
    // ThrowIfCancellationRequested; if a user toggles the DDGI plugin
    // off mid-shader-compile, the in-flight Task sees the cancel and
    // returns a faulted/cancelled result the wrapper handles without
    // ever allocating a pipeline against the disposed _atlas. Without
    // scoping this CTS to the plugin lifetime, repeated enable/disable
    // cycles would accumulate spent CancellationTokenSource instances
    // and leak their native wait-handle resources.
    private CancellationTokenSource? _lifecycleCts;

    /// <inheritdoc />
    public void Initialize(IEnginePluginHost host)
    {
        _host = host;
        // MARK: Fresh CancellationTokenSource per Initialize so a stale
        // token from a prior shutdown cannot suppress the new instance's
        // first pass-construction. Cancel-before-construct defense also
        // covers the case where Shutdown was called during a hot reload
        // and Initialize runs again with the same managed object.
        _lifecycleCts = new CancellationTokenSource();
        DDGIVolumeRegistry.Register(this, _volume);
        DDGIAtlasProviderRegistry.Register(Id, this);

        // Self-contained debug-view registration: the plugin owns the
        // "DDGI Probes" entry in the editor's viewport dropdown. The
        // toggle only flips the ShowProbes static; the DDGIDebugPass is
        // always present in the plan and self-gates in Execute, so no
        // render-plan rebuild is needed to show/hide probes.
        if (host is IEditorPluginHost editorHost)
        {
            editorHost.RegisterDebugView(
                Id,
                "DDGI Probes",
                on => DDGIVolumeRegistry.ShowProbes = on);
        }
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        DDGIAtlasProviderRegistry.Unregister(Id);
        DDGIVolumeRegistry.Unregister(this);
        // MARK: Cancel every in-flight ConstructPassWithTimeout Task
        // before disposing _atlas. The wrapped factory throw-checks
        // _lifecycleCts.Token as its first line; cancelling here flips
        // that check so an orphan sliver of shader/pipeline compile
        // work that survived the user's fast toggle-off lands in a
        // faulted/cancelled Task the wrapper discards without
        // registering into the next plan. Cancel then Dispose then
        // null so the field is ready for re-Initialize without a
        // dangling timer-handle leak.
        if (_lifecycleCts != null)
        {
            try { _lifecycleCts.Cancel(); }
            catch (AggregateException) { /* already-cancel is fine */ }
            _lifecycleCts.Dispose();
            _lifecycleCts = null;
        }
        _atlas?.Dispose();
        _atlas = null;
        _probeSnapshots = Array.Empty<DDGIProbePriority.ProbeSnapshot>();
        _atlasAllocFailed = false;
    }

    /// <inheritdoc />
    public void Dispose() => Shutdown();

    /// <inheritdoc />
    public RendererPluginPlan BuildPlan(RendererPluginContext context)
    {
        // MARK: Plugin-loaded heartbeat. If this line never
        // appears in the engine log, the DDGI plugin is not
        // enabled in the project's addons.json — the entire
        // BuildPlan including placement, update, warmup and the
        // debug viz pass is unreachable. Open the editor's
        // Plugins/addons UI and toggle DDGI on, then re-run.
        // Uses DiagnosticLogTicks rather than `== 0` so the first
        // placement/update wiring window remains visible even when
        // a prior plan build advanced the plugin tick.
        // `tickWindowEnd` exposes DiagnosticLogTicks so the user
        // can confirm the heartbeat fired inside the diagnostic
        // window rather than being masked by an over-aggressive
        // sample-rate constant.
        if (_tickCounter <= DiagnosticLogTicks)
        {
            Log.Info(
                $"[DDGI] BuildPlan first-window invocation " +
                $"(tick={_tickCounter}, tickWindowEnd={DiagnosticLogTicks}, " +
                $"atlas={(_atlas == null ? "null" : "ok")}, " +
                $"atlasProbeCount={_atlas?.UploadedProbeCount ?? -1}, " +
                $"atlasIrradSlot={(_atlas?.IrradianceBindlessIndex.ToString("X8") ?? "FFFFFFFF")}, " +
                $"volume={_volume?.IsInitialized switch { true => "initialized", false => "notInitialized", _ => "<null>" }}, " +
                $"warmupEnabledGlobally={DDGIAtlasResources.WarmupEnabledGlobally}, " +
                $"warmupEnabled={_atlas?.WarmupEnabled ?? false}, " +
                $"gpuPlaced={_atlas?.SparseLayoutGpuPlaced ?? false}, " +
                $"sparseReady={_atlas?.SparseLayoutReady ?? false}, " +
                $"fingerprint={_sceneFingerprint}, " +
                $"scene={context.Scene?.Name ?? "<none>"})",
                "DDGI");
        }

        EnsureAtlas(context);

        // MARK: Scene fingerprint reset must precede layout re-upload.
        long fingerprint = ComputeSceneFingerprint(context);
        if (fingerprint != _sceneFingerprint)
        {
            ++_fingerprintResetsTotal;
            // Surface churn (>=2 resets within 60 ticks) so the
            // disappearance audit can confirm Hypothesis B without
            // forcing the user to count manually. First 30 ticks
            // always log so the very first churn event is captured.
            long windowTicks = _tickCounter - _fingerprintResetsWindowStartTick;
            if (_tickCounter <= 30 ||
                _tickCounter % MaxLogSampleEvery == 0 ||
                (_fingerprintResetsTotal >= 2 && windowTicks <= 60))
            {
                int prevModelCount =
                    (int)(_sceneFingerprint & 0xFFFF);
                int prevLightCount =
                    (int)((_sceneFingerprint >> 16) & 0xFFFF);
                int prevContentHash =
                    (int)((_sceneFingerprint >> 32) & 0xFFFFFFFF);
                int newModelCount =
                    (int)(fingerprint & 0xFFFF);
                int newLightCount =
                    (int)((fingerprint >> 16) & 0xFFFF);
                int newContentHash =
                    (int)((fingerprint >> 32) & 0xFFFFFFFF);
                Log.Info(
                    $"[DDGI] fingerprint flip: " +
                    $"model {prevModelCount}->{newModelCount} " +
                    $"light {prevLightCount}->{newLightCount} " +
                    $"content 0x{prevContentHash:X}->0x{newContentHash:X} " +
                    $"resetsTotal={_fingerprintResetsTotal} " +
                    $"windowTicks={windowTicks}",
                    "DDGI");
                _fingerprintResetsWindowStartTick = _tickCounter;
            }
            _sceneFingerprint = fingerprint;
            _lastPlacementTick = -1;
            if (_atlas != null) _atlas.ResetSparseLayoutForSceneReload();
        }

        _tickCounter++;

        EnsureVolumeLayout(context);
        var plan = new RendererPluginPlan();

        if (ShouldKickPlacement(context))
        {
            try
            {
                string? placementSource = LocateShaderSource(
                    DDGIProbePlacementPass.PlacementShaderSource,
                    context,
                    _lastPlacementSearchedDirs);
                if (placementSource != null)
                {
                RenderPass? placement = ConstructPassWithTimeout(
                    "DDGI Probe Placement pass",
                    () => new DDGIProbePlacementPass(
                        context.Device,
                        context.World,
                        placementSource,
                        _atlas!,
                        context.ShaderIncludeDirs,
                        context.ShaderCliArgs,
                        context.SharedShaderCache),
                    TimeSpan.FromSeconds(60),
                    _lifecycleCts?.Token ?? CancellationToken.None);
                if (placement != null)
                {
                    plan.AddPass(placement);
                    _lastPlacementTick = _tickCounter;
                    if (_tickCounter <= DiagnosticLogTicks)
                    {
                        Log.Info(
                            $"[DDGI] registered DDGI Probe Placement " +
                            $"pass (tick={_tickCounter}, " +
                            $"sourceLength={placementSource.Length})",
                            "DDGI");
                    }
                }
                }
                else if (_tickCounter <= DiagnosticLogTicks ||
                         _tickCounter % MaxLogSampleEvery == 0)
                {
                    Log.Info(
                        $"[DDGI] probe-placement shader source not found " +
                        $"({DDGIProbePlacementPass.PlacementShaderSource}) " +
                        $"includeDirs=[{string.Join("; ", context.ShaderIncludeDirs ?? System.Array.Empty<string>())}] " +
                        $"contentRoot={context.ContentRoot} " +
                        $"(searched={_lastPlacementSearchedDirs.Count} " +
                        $"dirs: {string.Join("; ", _lastPlacementSearchedDirs)}); " +
                        $"pass NOT registered this frame",
                        "DDGI");
                }
            }
            catch (Exception exception)
            {
                Log.Error(
                    $"[DDGI] probe-placement pass wiring failed: " +
                    $"{exception.Message}",
                    "DDGI");
            }
        }

        // MARK: Light upload wrapped so a partial-upload throw does
        // not abort the entire BuildPlan and drop the probe-update
        // pass registration.
        try
        {
            _currentScene = context.Scene;
            UploadLightsSnapshot(context.Scene);
            BuildAndUploadLightTree();
        }
        catch (Exception lightException)
        {
            Log.Error(
                $"[DDGI] light upload failed (continuing without " +
                $"update lighting): {lightException.Message}",
                "DDGI");
            // MARK: Reset the light-tree SSBO to empty so the
            // probe-update shader's TraverseLightTree() early-out
            // (rootIdx >= nodeCount) prevents reading a half-
            // initialized tree on the next dispatch. Otherwise the
            // catch masks a GPU-side inconsistency that surfaces as
            // flicker artifacts during the next probe update.
            _atlas?.UploadLightTree(
                ReadOnlySpan<DDGILightTreeNode>.Empty,
                rootIndex: -1);
        }

        try
        {
            string? shaderSource = LocateProbeUpdateSource(context, _lastUpdateSearchedDirs);
            if (shaderSource != null)
            {
                RenderPass? update = ConstructPassWithTimeout(
                    "DDGI Probe Update pass",
                    () => new DDGIProbeUpdatePass(
                        context.Device,
                        context.World,
                        shaderSource,
                        context.ShaderIncludeDirs,
                        context.ShaderCliArgs,
                        _atlas!,
                        context.SharedShaderCache),
                    TimeSpan.FromSeconds(60),
                    _lifecycleCts?.Token ?? CancellationToken.None);
                if (update != null)
                {
                    plan.AddPass(update);
                    if (_tickCounter <= DiagnosticLogTicks)
                    {
                        Log.Info(
                            $"[DDGI] registered DDGI Probe Update pass " +
                            $"(tick={_tickCounter}, sourceLength=" +
                            $"{shaderSource.Length})",
                            "DDGI");
                    }
                }
            }
            else if (_tickCounter <= DiagnosticLogTicks ||
                     _tickCounter % MaxLogSampleEvery == 0)
            {
                Log.Info(
                    $"[DDGI] probe-update shader source not found " +
                    $"(ddgi_probe_update.slang) " +
                    $"includeDirs=[{string.Join("; ", context.ShaderIncludeDirs ?? System.Array.Empty<string>())}] " +
                    $"contentRoot={context.ContentRoot} " +
                    $"(searched={_lastUpdateSearchedDirs.Count} " +
                    $"dirs: {string.Join("; ", _lastUpdateSearchedDirs)}); " +
                    $"pass NOT registered this frame",
                    "DDGI");
            }
        }
        catch (Exception exception)
        {
            Log.Error(
                $"[DDGI] probe-update pass wiring failed: " +
                $"{exception.Message}",
                "DDGI");
        }

        // The GPU update pass is the sole atlas writer. The former CPU
        // warmup pass wrote a magenta diagnostic seed after every update,
        // which masked real probe lighting and made purple output look like
        // a renderer failure. Keep that diagnostic pass out of the runtime
        // plan; GPU placement and update now own the complete atlas lifecycle.

        // Always present so the ShowProbes toggle is a pure static
        // flag flip — no plan rebuild needed to show/hide probes.
        // DDGIDebugPass.Execute self-gates on DDGIVolumeRegistry.ShowProbes.
        if (_atlas != null && _host != null)
        {
            try
            {
                RenderPass? debug = ConstructPassWithTimeout(
                    "DDGI Probe (Debug) pass",
                    () => new DDGIDebugPass(
                        context.Device,
                        _volume,
                        _atlas,
                        _host,
                        context.ContentRoot,
                        context.ShaderCliArgs,
                        context.ShaderIncludeDirs,
                        context.SharedShaderCache),
                    TimeSpan.FromSeconds(60),
                    _lifecycleCts?.Token ?? CancellationToken.None);
                if (debug != null)
                {
                    plan.AddPostPass(debug);
                }
            }
            catch (Exception exception)
            {
                Log.Error(
                    $"[DDGI] debug pass wiring failed: {exception.Message}",
                    "DDGI");
            }
        }

        return plan;
    }

    internal IReadOnlyList<int> EvaluateFrameUpdates(Engine.RenderGraph.GpuWorkScheduler scheduler, out Vector3 cameraPos, out long frameNumber)
    {
        _tickCounter++;
        _priority.AdvanceFrame(_tickCounter);
        cameraPos = BuildCameraPosition();
        frameNumber = _tickCounter;

        DDGIProbePriority.CameraSnapshot camera = BuildCameraSnapshot();
        IReadOnlyList<int> updates = _priority.ScheduleProbeUpdates(
            _probeSnapshots,
            _lastInfluences,
            DDGIProbeUpdatePass.MaxProbesPerFrame,
            camera);

        int totalVolumeProbes = _probeSnapshots.Length;
        int admittedProbes = 0;
        bool hitBudgetCeiling = false;

        for (int i = 0; i < updates.Count; ++i)
        {
            if (scheduler.TryAdmit(GpuWorkDomain.Gi))
            {
                int probeIdx = updates[i];
                if (probeIdx >= 0 && probeIdx < _probeSnapshots.Length)
                {
                    _probeSnapshots[probeIdx] = new DDGIProbePriority.ProbeSnapshot(
                        Index: probeIdx,
                        Position: _volume.PositionAt(probeIdx),
                        LastUpdateFrame: _tickCounter);
                }
                admittedProbes++;
            }
            else
            {
                hitBudgetCeiling = true;
                break;
            }
        }

        int totalDeferredProbes = Math.Max(0, totalVolumeProbes - admittedProbes);
        int uncountedDeferred = totalDeferredProbes;
        if (hitBudgetCeiling && uncountedDeferred > 0)
        {
            uncountedDeferred--;
        }

        if (uncountedDeferred > 0)
        {
            scheduler.Defer(GpuWorkDomain.Gi, uncountedDeferred);
        }

        if (_tickCounter % LogSampleEvery == 0)
        {
            Log.Info(
                $"[DDGI] tick={_tickCounter} volumeProbes=" +
                $"{totalVolumeProbes} " +
                $"scheduling={updates.Count} admitted={admittedProbes} " +
                $"deferred={totalDeferredProbes}",
                "DDGI");
        }

        var admittedProbeIndices = new List<int>(admittedProbes);
        for (int i = 0; i < admittedProbes; ++i)
            admittedProbeIndices.Add(updates[i]);

        return admittedProbeIndices;
    }

    private static string? LocateProbeUpdateSource(
        RendererPluginContext context,
        List<string>? searchedDirectories = null)
        => LocateShaderSource(
            "ddgi_probe_update.slang",
            context,
            searchedDirectories);

    private static string? LocateShaderSource(
        string relativeName,
        RendererPluginContext context,
        List<string>? searchedDirectories = null)
    {
        // Reset per-tick audit log so failure diagnostics reflect
        // only this tick's probed paths, not cumulative history.
        searchedDirectories?.Clear();

        string[] candidates =
        {
            relativeName,
            Path.Combine("shaders", relativeName),
            Path.Combine(context.ContentRoot, "shaders", relativeName),
        };
        foreach (string includeDir in EnumerateIncludeDirs(context))
        {
            foreach (string relative in candidates)
            {
                string full = Path.Combine(includeDir, relative);
                searchedDirectories?.Add(full);
                if (File.Exists(full))
                    return File.ReadAllText(full);
            }
        }
        return null;
    }

    private static IEnumerable<string> EnumerateIncludeDirs(
        RendererPluginContext context)
    {
        if (context.ShaderIncludeDirs != null)
        {
            foreach (string dir in context.ShaderIncludeDirs)
                yield return dir;
        }
        if (!string.IsNullOrEmpty(context.ContentRoot))
        {
            yield return context.ContentRoot;
            yield return Path.Combine(context.ContentRoot, "shaders");
        }
    }

    /// <summary>
    /// Bounds a pass constructor in a <see cref="System.Threading.Tasks.Task{TResult}"/>
    /// so a hung Metal shader compile (<c>newLibraryWithSource</c>) or
    /// pipeline state creation
    /// (<c>newComputePipelineStateWithFunction</c> /
    /// <c>newRenderPipelineStateWithDescriptor</c>) cannot freeze the
    /// editor. Returns <c>null</c> on timeout, exception, or lifetime
    /// cancellation; the subsequent frame retries from the
    /// <see cref="ShaderCompileCache"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="lifetimeToken"/> is checked at the very start of
    /// the wrapped factory so a Shutdown-cancelled plugin's in-flight
    /// Task short-circuits before allocating a pipeline against the
    /// disposed _atlas. <c>Task.Run(wrappedFactory, lifetimeToken)</c>
    /// additionally short-circuits the Task itself if the token is
    /// already cancelled at scheduling time, surfacing a faulted/cancelled
    /// Task the wrapper catches via AggregateException so the editor's
    /// Debug log shows the cancellation cause rather than an opaque
    /// C# exception escalation.
    /// </remarks>
    private static RenderPass? ConstructPassWithTimeout(
        string passName,
        Func<RenderPass> factory,
        TimeSpan timeout,
        CancellationToken lifetimeToken)
    {
        Func<RenderPass> wrapped = () =>
        {
            lifetimeToken.ThrowIfCancellationRequested();
            return factory();
        };
        var task = System.Threading.Tasks.Task.Run(wrapped, lifetimeToken);
        try
        {
            // MARK: Three terminal states all funnel through one of two
            // paths here:
            //   * `return null` inside the if-branch when Wait times out
            //     (the Task is still running when control returns);
            //   * throw + catch when the Task is either Canceled (token
            //     was pre-cancelled, so Task.Run scheduled a Cancelled
            //     Task directly) or Faulted (the inner factory threw).
            // If Wait returns true the Task is RanToCompletion so the
            // post-try fall-through reads a valid `task.Result` — no
            // additional IsCanceled/IsFaulted guard needed.
            if (!task.Wait(timeout))
            {
                Log.Error(
                    $"[DDGI] {passName} constructor did not return within " +
                    $"{timeout.TotalSeconds:0}s; skipping this frame. " +
                    "Metal's newLibraryWithSource / " +
                    "newComputePipelineStateWithFunction can block for many " +
                    "seconds on the first RT compute-kernel compile; a " +
                    "concurrent reload path may have already populated the " +
                    "ShaderCompileCache so subsequent frames will resolve " +
                    "without reaching this timeout.",
                    "DDGI");
                return null;
            }
        }
        catch (AggregateException aggregateException)
        {
            // MARK: Cancelled + Faulted tasks both surface as
            // AggregateException via Task.Wait. OperationCanceledException
            // is the normal path for a Shutdown-cancelled lifetimeToken;
            // everything else is a constructor throw we still want to
            // log+null without re-throwing into the build-plan caller.
            foreach (Exception inner in aggregateException.InnerExceptions)
            {
                if (inner is OperationCanceledException)
                {
                    Log.Info(
                        $"[DDGI] {passName} constructor cancelled by plugin shutdown; skipping this frame.",
                        "DDGI");
                    return null;
                }
            }
            Exception fault = aggregateException.GetBaseException();
            Log.Error(
                $"[DDGI] {passName} constructor threw: {fault.Message}",
                "DDGI");
            return null;
        }
        return task.Result;
    }

    private Vector3 BuildCameraPosition()
    {
        if (_host != null &&
            _host.TryGetActiveCameraData(
                1, 1,
                out Vector3 camPos,
                out _,
                out _))
        {
            return camPos;
        }
        return Vector3.Zero;
    }

    private void EnsureVolumeLayout(RendererPluginContext context)
    {
        if (_volume.IsInitialized && _atlas != null && _atlas.SparseLayoutReady)
            return;

        _volume.InitializeGpuOwned();
        return;

        /*
        var positions = new List<Vector3>();
        int res = _volume.BaseGridResolution; // 32
        int[] gridToProbeIndex = new int[res * res * res];
        Array.Fill(gridToProbeIndex, -1);

        Vector3 origin = _volume.Origin;
        Vector3 extent = _volume.Extent;
        Vector3 volumeMin = origin - extent;
        Vector3 cellSize = extent * 2.0f / res;

        // Step by 2 along each axis -> 16 * 16 * 16 = 4096 probes
        int step = 2;
        int probeIdx = 0;
        for (int z = 0; z < res; z += step)
        {
            for (int y = 0; y < res; y += step)
            {
                for (int x = 0; x < res; x += step)
                {
                    Vector3 pos = volumeMin + (new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) * cellSize);
                    positions.Add(pos);

                    // Map the 2x2x2 block of coarse cells to this probe
                    for (int dz = 0; dz < step; ++dz)
                    {
                        for (int dy = 0; dy < step; ++dy)
                        {
                            for (int dx = 0; dx < step; ++dx)
                            {
                                int cx = Math.Min(x + dx, res - 1);
                                int cy = Math.Min(y + dy, res - 1);
                                int cz = Math.Min(z + dz, res - 1);
                                int linear = cz * res * res + cy * res + cx;
                                gridToProbeIndex[linear] = probeIdx;
                            }
                        }
                    }

                    probeIdx++;
                }
            }
        }

        Vector3[] posArray = positions.ToArray();
        _volume.Initialize(posArray, gridToProbeIndex);

        if (_atlas != null)
        {
            _atlas.UploadSparseLayout(posArray, gridToProbeIndex);
        }
        */
    }

    private void RefreshProbeSnapshots()
    {
        // Probe positions are GPU-owned. CPU priority scheduling is no
        // longer used to select update slots; the update kernel consumes
        // the placement counter directly.
        _probeSnapshots = Array.Empty<DDGIProbePriority.ProbeSnapshot>();
    }

    private bool ShouldKickPlacement(RendererPluginContext context)
    {
        if (_atlas == null) return false;
        // Placement is a persistent GPU pass. It rebuilds the sparse
        // layout and relocates probes every frame from scene geometry;
        // no CPU position list or placement readback participates.
        return _atlas != null;

        /*
        // MARK: gate on ACTUAL placement dispatch success, not the
        // atlas sample-readiness flag. SparseLayoutReady flips true
        // from three independent paths (CPU seed in EnsureVolumeLayout,
        // UploadEmptySparseLayout in the failure-mode branches of the
        // placement pass, and MarkSparseLayoutReady after a real
        // dispatch). Keys off SparseLayoutGpuPlaced — which only
        // MarkSparseLayoutReady writes — so the CPU seed cannot pre-empt
        // the GPU placement run for the current scene. See
        // docs/renderer/ddgi.md#placement-race.
        if (_atlas.SparseLayoutGpuPlaced) return false;

        // MARK: defer placement until ECS has a ModelComponent — see docs/renderer/ddgi.md#placement-race.
        int entityCount = 0;
        foreach (ulong entity in context.World.Entities)
        {
            ++entityCount;
            if (context.World.TryGet<Engine.RHI.ModelComponent>(entity, out _))
                return true;
        }
        if (_tickCounter <= DiagnosticLogTicks ||
            _tickCounter % MaxLogSampleEvery == 0)
        {
            Log.Info(
                $"[DDGI] ShouldKickPlacement skipped: " +
                $"entityCount={entityCount} (no entity carries ModelComponent)",
                "DDGI");
        }
        return false;
        */
    }

    private long ComputeSceneFingerprint(RendererPluginContext context)
    {
        if (context.Scene == null) return 0;
        int modelCount = context.Scene.Models?.Count ?? 0;
        int lightCount = context.Scene.Lights?.Count ?? 0;
        int contentHash = context.ContentRoot == null
            ? 0
            : context.ContentRoot.GetHashCode();
        long combined = (long)modelCount;
        unchecked
        {
            combined |= (long)lightCount << 16;
            combined ^= (long)contentHash << 32;
        }
        return combined;
    }

    private DDGIProbePriority.LightInfluence[] BuildLightInfluences(
        SceneGraph scene)
    {
        if (scene?.Lights is null || scene.Lights.Count == 0)
            return [];

        var influences =
            new DDGIProbePriority.LightInfluence[scene.Lights.Count];

        // IsDirty fires ONCE per scene-load lifecycle, then turns
        // off. Per-frame marking would silently degrade the boost
        // to a constant bias — the responsiveness behaviour the
        // user asked for ("responsive to lighting changes") is
        // delivered by the scene-mutating hot-reload path, not by
        // a per-frame signal here.
        bool seedDirty = !_lightsSeeded;
        _lightsSeeded = true;

        for (int i = 0; i < scene.Lights.Count; ++i)
        {
            LightNode node = scene.Lights[i];
            Vector3 position = ReadFloat3(node.Position, Vector3.Zero);

            float radius = string.Equals(node.Type, "directional",
                StringComparison.OrdinalIgnoreCase)
                ? 1.0e6f
                : Math.Max(node.Range, 0.001f);

            influences[i] = new DDGIProbePriority.LightInfluence(
                LightId: i,
                Position: position,
                Radius: radius,
                IsDirty: seedDirty,
                DirtyFramesRemaining: seedDirty ? 1 : 0);
        }
        return influences;
    }

    private DDGIProbePriority.CameraSnapshot BuildCameraSnapshot()
    {
        Vector3 position = Vector3.Zero;
        Vector3 forward = new(0f, 0f, -1f);
        Vector3 up = Vector3.UnitY;
        Vector3 right = Vector3.Cross(up, forward);
        float fovRadians = (float)Math.PI / 3f;
        float nearDistance = 0.1f;
        float aspectRatio = 16f / 9f;

        if (_host != null &&
            _host.TryGetActiveCameraData(
                1, 1,
                out Vector3 hostCamPos,
                out _,
                out System.Numerics.Matrix4x4 invViewProj))
        {
            position = hostCamPos;
            // invViewProj's first three columns hold invView's basis
            // vectors scaled by projection aspect/FOV. Normalize to
            // recover the world-space orientation regardless of the
            // projection baked into the lengths.
            Vector3 col0 = new(
                invViewProj.M11, invViewProj.M21, invViewProj.M31);
            Vector3 col1 = new(
                invViewProj.M12, invViewProj.M22, invViewProj.M32);
            Vector3 col2 = new(
                invViewProj.M13, invViewProj.M23, invViewProj.M33);
            if (col0.LengthSquared() > 1e-6f &&
                col1.LengthSquared() > 1e-6f &&
                col2.LengthSquared() > 1e-6f)
            {
                right = Vector3.Normalize(col0);
                up = Vector3.Normalize(col1);
                forward = Vector3.Normalize(-col2);
            }
        }

        return new DDGIProbePriority.CameraSnapshot(
            Position: position,
            Forward: forward,
            Up: up,
            Right: right,
            FieldOfViewRadians: fovRadians,
            NearDistance: nearDistance,
            AspectRatio: aspectRatio);
    }

    private static Vector3 ReadFloat3(float[] source, Vector3 fallback)
    {
        if (source is null || source.Length < 3)
            return fallback;
        return new Vector3(source[0], source[1], source[2]);
    }

    private void UploadLightsSnapshot(SceneGraph scene)
    {
        if (_atlas == null) return;
        int capacity = _atlas.LightSlotCount;
        if (capacity <= 0) return;

        var lights = scene?.Lights;
        int count = lights != null ? Math.Min(lights.Count, capacity) : 0;
        var snapshot = new DDGILightSnapshot[count];

        for (int i = 0; i < count; ++i)
        {
            LightNode node = lights![i];
                Vector3 position = ReadFloat3(node.Position, Vector3.Zero);
                Vector3 color = ReadFloat3(node.Color, Vector3.One);
                float range = node.Range > 0f ? node.Range : 1.0e6f;
                float intensity = node.Intensity > 0f ? node.Intensity : 0f;

                float type = string.Equals(node.Type, "point",
                    StringComparison.OrdinalIgnoreCase) ? 1f
                    : string.Equals(node.Type, "spot",
                        StringComparison.OrdinalIgnoreCase) ? 2f
                    : 0f;

                Vector3 dir = ReadFloat3(node.Direction, new Vector3(0, -1, 0));
                if (dir.LengthSquared() < 0.0001f) dir = new Vector3(0, -1, 0);
                Vector3 axis = Vector3.Normalize(dir);

                float innerAngle = 0.0f;
                float outerAngle = 0.0f;
                if (node.InnerCone is float inner) innerAngle = inner;
                if (node.OuterCone is float outer) outerAngle = outer;

                snapshot[i] = new DDGILightSnapshot
                {
                    Position = new Vector4(
                        position.X, position.Y, position.Z, range),
                    Direction = new Vector4(
                        axis.X, axis.Y, axis.Z, type),
                    Color = new Vector4(
                        color.X, color.Y, color.Z, intensity),
                    ShapeParams = new Vector4(
                        innerAngle, outerAngle, 0f, 0f),
                };
            }

        _currentLightSnapshot = snapshot;
        if (snapshot.Length > 0)
        {
            _atlas.UploadLights(snapshot);
        }
    }

    /// <summary>Called every frame from <see cref="DDGIProbeUpdatePass.Execute"/>
    /// to keep the GPU light buffer and BVH tree in sync with the current
    /// scene state. This allows GI to react immediately to light edits
    /// without waiting for the render plan to be recompiled.</summary>
    public void RefreshLightsForFrame()
    {
        if (_atlas == null || _currentScene == null) return;
        UploadLightsSnapshot(_currentScene);
        BuildAndUploadLightTree();
    }

    private void EnsureAtlas(RendererPluginContext context)
    {
        if (_atlas != null || _atlasAllocFailed) return;
        try
        {
            _atlas = new DDGIAtlasResources(
                context.Device,
                context.BindlessHeap,
                new Engine.DDGI.Vector3I(
                    _volume.BaseGridResolution,
                    _volume.BaseGridResolution,
                    _volume.BaseGridResolution),
                _volume.Origin,
                _volume.Extent,
                maxLights: 128,
                maxProbesTotalBudget: _volume.MaxProbesTotalBudget);

            if (_tickCounter % MaxLogSampleEvery == 0)
            {
                Log.Info(
                    $"[DDGI] atlas allocated baseGrid=" +
                    $"{_volume.BaseGridResolution}^3 " +
                    $"maxProbes={_volume.MaxProbesTotalBudget} " +
                    $"irradSlot={_atlas.IrradianceBindlessIndex} " +
                    $"visSlot={_atlas.VisibilityBindlessIndex}",
                    "DDGI");
            }
        }
        catch (Exception exception)
        {
            _atlasAllocFailed = true;
            Log.Error(
                $"[DDGI] atlas allocation failed: {exception.Message}",
                "DDGI");
        }
    }

    /// <inheritdoc />
    public (uint IrradianceBindlessIndex, uint VisibilityBindlessIndex)
        GetAtlasBindlessSlots()
    {
        if (_atlas == null) return (0u, 0u);
        return (_atlas.IrradianceBindlessIndex,
                _atlas.VisibilityBindlessIndex);
    }

    /// <inheritdoc />
    public bool TryGetSparseBuffers(
        out Engine.RHI.RhiBuffer probePositions,
        out Engine.RHI.RhiBuffer gridToProbeIndex,
        out Engine.RHI.RhiBuffer probeCounter)
    {
        probePositions = null!;
        gridToProbeIndex = null!;
        probeCounter = null!;
        if (_atlas == null) return false;
        probePositions = _atlas.ProbePositions;
        gridToProbeIndex = _atlas.GridToProbeIndex;
        probeCounter = _atlas.ProbeCounter;
        return true;
    }

    /// <inheritdoc />
    public DDGIAtlasResourceHandles ResourceHandles =>
        _atlas?.ResourceHandles ?? default;

    /// <inheritdoc />
    public bool TryGetExternalResources(
        out DDGIAtlasExternalResources resources)
    {
        resources = default;
        if (_atlas == null)
            return false;
        resources = new DDGIAtlasExternalResources(
            _atlas.ProbePositions,
            _atlas.GridToProbeIndex,
            _atlas.ProbeCounter,
            _atlas.ProbeDrawArgs,
            _atlas.Lights,
            _atlas.LightTreeNodes,
            _atlas.Irradiance,
            _atlas.Visibility);
        return true;
    }

    /// <inheritdoc />
    public bool IsSparseLayoutReady =>
        _atlas != null &&
        _atlas.SparseLayoutReady &&
        _lastPlacementTick > 0 &&
        (_tickCounter - _lastPlacementTick) >= SparseLayoutWarmupTicks;

    /// <inheritdoc />
    public bool TryGetProbeVolume(
        out Vector3 origin,
        out Vector3 extent,
        out Engine.RenderGraph.Vector3I gridResolution)
    {
        origin = _volume.Origin;
        extent = _volume.Extent;
        gridResolution = new Engine.RenderGraph.Vector3I(
            _volume.BaseGridResolution,
            _volume.BaseGridResolution,
            _volume.BaseGridResolution);
        return _atlas != null;
    }

    /// <inheritdoc />
    public int RaysPerProbe => DDGIProbeUpdatePass.RaysPerProbe;

    /// <inheritdoc />
    public int MaxProbesPerFrame =>
        DDGIProbeUpdatePass.MaxProbesPerFrame;

    /// <inheritdoc />
    public bool TryGetLightTree(
        out Engine.RHI.RhiBuffer treeBuffer,
        out uint nodeCount,
        out uint rootIndex)
    {
        treeBuffer = null!;
        nodeCount = 0u;
        rootIndex = 0u;
        if (_atlas == null || _atlas.TreeNodeCount <= 0)
            return false;
        treeBuffer = _atlas.LightTreeNodes;
        nodeCount = (uint)_atlas.TreeNodeCount;
        rootIndex = (uint)Math.Max(0, _atlas.TreeRootIndex);
        return true;
    }

    private void BuildAndUploadLightTree()
    {
        if (_atlas == null) return;
        if (_currentLightSnapshot.Length == 0)
        {
            _atlas.UploadLightTree(
                ReadOnlySpan<DDGILightTreeNode>.Empty,
                rootIndex: -1);
            return;
        }

        Span<DDGILightSnapshot> liveSpan = _currentLightSnapshot.AsSpan(
            0, Math.Min(
                _currentLightSnapshot.Length,
                _atlas.LightSlotCount));
        DDGILightTreeNode[] nodes =
            _lightTreeBuilder.BuildCpu(liveSpan, out int rootIndex);

        if (_atlas.LightSlotCount > 0 && liveSpan.Length > 0)
        {
            ReadOnlySpan<DDGILightSnapshot> reordered =
                new ReadOnlySpan<DDGILightSnapshot>(
                    _currentLightSnapshot, 0, liveSpan.Length);
            _atlas.UploadLights(reordered);
        }

        _atlas.UploadLightTree(nodes, rootIndex);

        if (_tickCounter % MaxLogSampleEvery == 0)
        {
            Log.Info(
                $"[DDGI] light tree rebuilt tick={_tickCounter} " +
                $"nodes={nodes.Length} leaves={CountLeaves(nodes)} " +
                $"root={rootIndex} leafBudget={LeafVisitBudget}",
                "DDGI");
        }
    }

    private static int CountLeaves(ReadOnlySpan<DDGILightTreeNode> nodes)
    {
        if (nodes.Length == 0) return 0;
        int count = 0;
        for (int i = 0; i < nodes.Length; ++i)
        {
            uint raw = BitConverter.SingleToUInt32Bits(nodes[i].MinData0.W);
            if ((raw & DDGILightTreeNode.LeafBit) != 0u)
                ++count;
        }
        return count;
    }

    private const int MaxLogSampleEvery = 60;
    private const int SparseLayoutWarmupTicks = 0;

    /// <summary>First-N-ticks window during which the plugin
    /// logs every registration outcome (success, skip, exception)
    /// regardless of the steady-state sampling rate. After the
    /// window, sampling degrades to <see cref="MaxLogSampleEvery"/>
    /// so steady-state logs stay sparse.</summary>
    private const int DiagnosticLogTicks = 30;

    private readonly List<string> _lastUpdateSearchedDirs = new();
    private readonly List<string> _lastPlacementSearchedDirs = new();

    /// <summary>Per-probe cap on the number of light-tree leaves
    /// visited by the probe-update kernel's hierarchical traversal.
    /// Cross-file consumers (like <see cref="DDGIProbeUpdatePass"/>)
    /// pull this from the push struct; bump it together with the
    /// shader's <c>kWorklistCap</c> if tree depth grows past the
    /// current frontier headroom.</summary>
    public const int LeafVisitBudget = 4;
}

