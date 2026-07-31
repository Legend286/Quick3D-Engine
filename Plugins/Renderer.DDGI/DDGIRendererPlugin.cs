// SPDX-License-Identifier: MIT
using Engine.CBindings;
using Engine.Plugins;
using Engine.RenderGraph;
using Engine.Scene;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

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


    private readonly DDGIProbeVolume _volume;
    private readonly DDGIProbePriority.Tuning _tuning;
    private readonly DDGIProbePriority _priority;
    private DDGIProbePriority.ProbeSnapshot[] _probeSnapshots =
        Array.Empty<DDGIProbePriority.ProbeSnapshot>();
    private bool _lightsSeeded;
    private long _tickCounter;
    private long _lastPlacementTick = -1;

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

    /// <inheritdoc />
    public void Initialize(IEnginePluginHost host)
    {
        _host = host;
        DDGIVolumeRegistry.Register(this, _volume);
        DDGIAtlasProviderRegistry.Register(Id, this);
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        DDGIAtlasProviderRegistry.Unregister(Id);
        DDGIVolumeRegistry.Unregister(this);
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
        EnsureAtlas(context);
        _tickCounter++;
        _priority.AdvanceFrame(_tickCounter);

        var plan = new RendererPluginPlan();

        if (ShouldKickPlacement())
        {
            try
            {
                string? placementSource = LocateShaderSource(
                    DDGIProbePlacementPass.PlacementShaderSource, context);
                if (placementSource != null)
                {
                    plan.AddPass(new DDGIProbePlacementPass(
                        context.Device,
                        context.World,
                        placementSource,
                        _atlas!,
                        context.ShaderIncludeDirs,
                        context.ShaderCliArgs,
                        context.SharedShaderCache));
                    _lastPlacementTick = _tickCounter;
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

        if (!_volume.IsInitialized)
        {
            if (_tickCounter % LogSampleEvery == 0)
            {
                Log.Info(
                    $"[DDGI] tick={_tickCounter} sparse layout pending " +
                    $"placement pass; deferring Gi admission",
                    "DDGI");
            }
            return plan;
        }

        DDGIProbePriority.LightInfluence[] influences =
            BuildLightInfluences(context.Scene);

        RefreshProbeSnapshots();

        DDGIProbePriority.CameraSnapshot camera = BuildCameraSnapshot();

        IReadOnlyList<int> updates = _priority.ScheduleProbeUpdates(
            _probeSnapshots,
            influences,
            DDGIProbeUpdatePass.MaxProbesPerFrame,
            camera);

        int admittedProbes = 0;
        foreach (int _ in updates)
        {
            if (context.GpuWorkScheduler.TryAdmit(GpuWorkDomain.Gi))
                admittedProbes++;
        }
        int deferredProbes = updates.Count - admittedProbes;
        if (deferredProbes > 0)
            context.GpuWorkScheduler.Defer(
                GpuWorkDomain.Gi, deferredProbes);

        if (_tickCounter % LogSampleEvery == 0)
        {
            Log.Info(
                $"[DDGI] tick={_tickCounter} volumeProbes=" +
                $"{_probeSnapshots.Length} lightsDirty={influences.Length} " +
                $"scheduling={updates.Count} admitted={admittedProbes} " +
                $"deferred={deferredProbes}",
                "DDGI");
        }

        if (admittedProbes == 0)
            return plan;

        var admittedProbeIndices = new List<int>(admittedProbes);
        for (int i = 0; i < admittedProbes; ++i)
            admittedProbeIndices.Add(updates[i]);

        try
        {
            string? shaderSource = LocateProbeUpdateSource(context);
            if (shaderSource == null)
            {
                Log.Warn(
                    "[DDGI] probe-update shader missing in every include " +
                    "dir; schedule will roll forward next tick",
                    "DDGI");
                return plan;
            }

            plan.AddPass(new DDGIProbeUpdatePass(
                context.Device,
                context.World,
                shaderSource,
                context.ShaderIncludeDirs,
                context.ShaderCliArgs,
                _atlas!,
                admittedProbeIndices,
                BuildCameraPosition(),
                _tickCounter,
                context.SharedShaderCache));
        }
        catch (Exception exception)
        {
            Log.Error(
                $"[DDGI] probe-update pass wiring failed: " +
                $"{exception.Message}",
                "DDGI");
        }

        return plan;
    }

    private static string? LocateProbeUpdateSource(
        RendererPluginContext context)
        => LocateShaderSource("ddgi_probe_update.slang", context);

    private static string? LocateShaderSource(
        string relativeName,
        RendererPluginContext context)
    {
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
            yield return context.ContentRoot;
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

    private void RefreshProbeSnapshots()
    {
        int probeCount = _volume.ProbeCount;
        if (_probeSnapshots.Length != probeCount)
            _probeSnapshots =
                new DDGIProbePriority.ProbeSnapshot[probeCount];
        for (int i = 0; i < probeCount; ++i)
        {
            _probeSnapshots[i] = new DDGIProbePriority.ProbeSnapshot(
                Index: i,
                Position: _volume.PositionAt(i),
                LastUpdateFrame: _tickCounter - 1);
        }
    }

    private bool ShouldKickPlacement()
    {
        if (_atlas == null) return false;
        if (_volume.IsInitialized) return false;
        if (_lastPlacementTick > 0) return false;
        return true;
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

    private const int MaxLogSampleEvery = 60;
    private const int SparseLayoutWarmupTicks = 3;
}

