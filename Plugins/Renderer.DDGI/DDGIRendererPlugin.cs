// SPDX-License-Identifier: MIT
using Engine.CBindings;
using Engine.Plugins;
using Engine.RenderGraph;
using Engine.RHI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace Engine.DDGI;

/// <summary>
/// DDGI renderer plugin. Owns the scrolling probe volume,
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

    private readonly DDGIProbeVolume _volume;
    private long _tickCounter;

    public DDGIRendererPlugin()
    {
        _volume = new DDGIProbeVolume(
            new Vector3(0f, 0f, 0f),
            new Vector3(2f, 2f, 2f),
            gridResolution: DDGIProbeVolume.DefaultBaseGridResolution,
            clipmapLevelCount:
                DDGIProbeVolume.DefaultClipmapLevelCount,
            clipmapScale: DDGIProbeVolume.DefaultClipmapScale,
            maxProbesTotalBudget:
                DDGIProbeVolume.DefaultMaxProbesTotalBudget);

        _tickCounter = 0;
    }

    private DDGIAtlasResources? _atlas;
    private Engine.Scene.SceneGraph? _atlasScene;
    private RaytracingSceneCache? _sceneCache;
    private bool _atlasAllocFailed;
    private bool _gpuPlanReady;
    private IEnginePluginHost? _host;

    /// <inheritdoc />
    public void Initialize(IEnginePluginHost host)
    {
        _host = host;
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
                "DDGI Indirect",
                on => DDGIVolumeRegistry.ShowIndirect = on);
            editorHost.RegisterDebugView(
                Id,
                "DDGI Probes",
                on => DDGIVolumeRegistry.ShowProbes = on);
            editorHost.RegisterDebugViewToggle(
                Id,
                "DDGI Probes",
                "Probe status colours",
                DDGIVolumeRegistry.ShowProbeStatusColors,
                on => DDGIVolumeRegistry.ShowProbeStatusColors = on);
        }
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        DDGIAtlasProviderRegistry.Unregister(Id);
        DDGIVolumeRegistry.Unregister(this);
        _gpuPlanReady = false;
        DDGIAtlasResources? atlas = _atlas;
        _atlas = null;
        _atlasScene = null;
        RaytracingSceneCache? sceneCache = _sceneCache;
        _sceneCache = null;
        atlas?.Dispose();
        sceneCache?.Dispose();
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
                $"[DDGI] Building GPU-scrolling probe plan " +
                $"(scene={context.Scene?.Name ?? "<none>"})",
                "DDGI");
        }

        IActiveCameraDataProvider? cameraProvider =
            context.ActiveCameraProvider ?? _host;
        EnsureAtlas(context);
        _gpuPlanReady = false;

        _tickCounter++;

        var plan = new RendererPluginPlan();
        bool placementRegistered = false;
        bool scheduleRegistered = false;
        bool updateRegistered = false;

        if (_atlas != null && cameraProvider != null)
        {
            try
            {
                string? placementSource = LocateShaderSource(
                    DDGIProbePlacementPass.PlacementShaderSource,
                    context,
                    _lastPlacementSearchedDirs);
                string? resetSource = LocateShaderSource(
                    "ddgi_probe_reset.slang",
                    context);
                if (placementSource != null && resetSource != null)
                {
                    plan.AddPass(new DDGIProbeResetPass(
                        context.Device,
                        resetSource,
                        _atlas!,
                        context.ShaderIncludeDirs,
                        context.ShaderCliArgs,
                        context.SharedShaderCache));
                    RenderPass placement = new DDGIProbePlacementPass(
                        context.Device,
                        _sceneCache!,
                        placementSource,
                        _atlas!,
                        cameraProvider,
                        context.GpuWorkScheduler,
                        context.SceneGpuDataProvider,
                        context.ShaderIncludeDirs,
                        context.ShaderCliArgs,
                        context.SharedShaderCache);
                    plan.AddPass(placement);
                    placementRegistered = true;
                    if (_tickCounter <= DiagnosticLogTicks)
                    {
                        Log.Info(
                            $"[DDGI] registered DDGI Probe Placement " +
                            $"pass (tick={_tickCounter}, " +
                            $"sourceLength={placementSource.Length})",
                            "DDGI");
                    }
                }
                else if (_tickCounter <= DiagnosticLogTicks ||
                         _tickCounter % MaxLogSampleEvery == 0)
                {
                    Log.Info(
                        "[DDGI] probe reset or placement shader source not found " +
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

        string? scheduleSource = LocateShaderSource(
            "ddgi_probe_schedule.slang",
            context);
        if (scheduleSource != null &&
            _atlas != null &&
            cameraProvider != null)
        {
            plan.AddPass(new DDGIProbeSchedulePass(
                context.Device,
                scheduleSource,
                _atlas,
                cameraProvider,
                context.GpuWorkScheduler,
                context.SceneGpuDataProvider,
                context.ShaderIncludeDirs,
                context.ShaderCliArgs,
                context.SharedShaderCache));
            scheduleRegistered = true;
        }

        try
        {
            string? shaderSource = LocateProbeUpdateSource(context, _lastUpdateSearchedDirs);
            if (shaderSource != null &&
                _atlas != null &&
                cameraProvider != null)
            {
                RenderPass update = new DDGIProbeUpdatePass(
                    context.Device,
                    _sceneCache!,
                    shaderSource,
                    context.ShaderIncludeDirs,
                    context.ShaderCliArgs,
                    _atlas!,
                    cameraProvider!,
                    context.SceneGpuDataProvider,
                    context.SharedShaderCache);
                plan.AddPass(update);
                updateRegistered = true;
                if (_tickCounter <= DiagnosticLogTicks)
                {
                    Log.Info(
                        $"[DDGI] registered DDGI Probe Update pass " +
                        $"(tick={_tickCounter}, sourceLength=" +
                        $"{shaderSource.Length})",
                        "DDGI");
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

        _gpuPlanReady = placementRegistered &&
            scheduleRegistered &&
            updateRegistered;

        if (_atlas != null && cameraProvider != null)
        {
            try
            {
                RenderPass debug = new DDGIDebugPass(
                    context.Device,
                    _atlas,
                    cameraProvider,
                    context.ContentRoot,
                    context.ShaderCliArgs,
                    context.ShaderIncludeDirs,
                    context.SharedShaderCache);
                plan.AddPostPass(debug);
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

    private void EnsureAtlas(RendererPluginContext context)
    {
        if (_atlas != null && !ReferenceEquals(_atlasScene, context.Scene))
        {
            _sceneCache?.Dispose();
            _sceneCache = null;
            _atlas.Dispose();
            _atlas = null;
            _atlasAllocFailed = false;
        }
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
                _volume.BaseCellSize,
                _volume.ClipmapLevelCount,
                _volume.ClipmapScale,
                maxProbesTotalBudget: _volume.MaxProbesTotalBudget);
            _sceneCache = new RaytracingSceneCache(
                context.Device,
                context.World);
            _atlasScene = context.Scene;
            if (_tickCounter % MaxLogSampleEvery == 0)
            {
                Log.Info(
                    $"[DDGI] atlas allocated baseGrid=" +
                    $"{_volume.BaseGridResolution}^3 " +
                    $"x{_volume.ClipmapLevelCount} clipmaps " +
                    $"maxProbes={_volume.MaxProbesTotalBudget} " +
                    $"irradSlot={_atlas.IrradianceBindlessIndex} " +
                    $"visSlot={_atlas.VisibilityBindlessIndex} " +
                    $"specSlot={_atlas.SpecularRadianceBindlessIndex}",
                    "DDGI");
            }
        }
        catch (Exception exception)
        {
            _sceneCache?.Dispose();
            _sceneCache = null;
            _atlas?.Dispose();
            _atlas = null;
            _atlasScene = null;
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
    public uint GetSpecularBindlessSlot()
        => _atlas?.SpecularRadianceBindlessIndex ??
            RhiBindlessHeap.InvalidSlot;

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
    public bool TryGetPersistentLookup(
        out Engine.RHI.RhiBuffer probeWorldKeys,
        out Engine.RHI.RhiBuffer worldProbeHash,
        out uint hashCapacity)
    {
        probeWorldKeys = null!;
        worldProbeHash = null!;
        hashCapacity = 0u;
        if (_atlas == null)
            return false;
        probeWorldKeys = _atlas.ProbeWorldKeys;
        worldProbeHash = _atlas.WorldProbeHash;
        hashCapacity = (uint)_atlas.WorldProbeHashCapacity;
        return true;
    }

    /// <inheritdoc />
    public bool TryGetGpuProbeState(
        out Engine.RHI.RhiBuffer probeStates,
        out Engine.RHI.RhiBuffer probeUpdateQueue,
        out Engine.RHI.RhiBuffer volumeState)
    {
        probeStates = null!;
        probeUpdateQueue = null!;
        volumeState = null!;
        if (_atlas == null)
            return false;
        probeStates = _atlas.ProbeStates;
        probeUpdateQueue = _atlas.ProbeUpdateQueue;
        volumeState = _atlas.VolumeState;
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
            _atlas.ProbeWorldKeys,
            _atlas.WorldProbeHash,
            _atlas.ProbeCounter,
            _atlas.ProbeDrawArgs,
            _atlas.ProbeStates,
            _atlas.ProbeSpecularStates,
            _atlas.ProbeUpdateQueue,
            _atlas.VolumeState,
            _atlas.Irradiance,
            _atlas.Visibility,
            _atlas.SpecularRadiance);
        return true;
    }

    /// <inheritdoc />
    public bool IsSparseLayoutReady =>
        _gpuPlanReady;

    /// <inheritdoc />
    public uint ConsumerFlags =>
        DDGIVolumeRegistry.ShowIndirect ? 1u : 0u;

    /// <inheritdoc />
    public bool HasPendingWork =>
        _atlas != null &&
        (_atlas.SceneBakeActive || _atlas.RadianceRefreshActive);

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

    private const int MaxLogSampleEvery = 60;
    /// <summary>First-N-ticks window during which the plugin
    /// logs every registration outcome (success, skip, exception)
    /// regardless of the steady-state sampling rate. After the
    /// window, sampling degrades to <see cref="MaxLogSampleEvery"/>
    /// so steady-state logs stay sparse.</summary>
    private const int DiagnosticLogTicks = 30;

    private readonly List<string> _lastUpdateSearchedDirs = new();
    private readonly List<string> _lastPlacementSearchedDirs = new();

}
