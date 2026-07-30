// SPDX-License-Identifier: MIT
using Engine.CBindings;
using Engine.Plugins;
using Engine.Renderer;
using Engine.Scene;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Engine.DDGI;

/// <summary>
/// DDGI renderer plugin. Phase-2 wiring: hosts the Phase-1 CPU
/// primitives (<see cref="DDGIProbeVolume"/>, <see cref="DDGIProbePriority"/>)
/// and exercises them on every <see cref="BuildPlan"/> call so
/// toggle-on is observable as a measured <c>[DDGI]</c> log line +
/// admission count against the <see cref="GpuWorkDomain.Gi"/>
/// budget. The light-BVH is built but its traversal remains
/// Phase-3 work; this commit ships only the budget gate + scene
/// scheduling path.
/// </summary>
public sealed class DDGIRendererPlugin :
    IEnginePlugin,
    IRendererPlanPlugin,
    IRendererPlanPluginLite
{
    /// <inheritdoc />
    public string Id => "renderer.ddgi";

    private const int MaxProbesPerFrame = 8;
    private const int LogSampleEvery = 60;

    private readonly DDGIProbeVolume _volume;
    private readonly DDGIProbePriority.Tuning _tuning;
    private readonly DDGIProbePriority _priority;
    private readonly DDGIProbePriority.ProbeSnapshot[] _probeSnapshots;
    private bool _lightsSeeded;
    private long _tickCounter;

    public DDGIRendererPlugin()
    {
        _volume = new DDGIProbeVolume(
            new Vector3(0f, 0f, 0f),
            new Vector3(32f, 16f, 32f),
            gridResolution: 8);

        _tuning = new DDGIProbePriority.Tuning(
            DistanceWeight: 1.0f,
            DistanceFalloffMeters: 24f,
            FrustumContainmentBonus: 0.5f,
            StalePenaltyPerFrame: 0.05f,
            StalePenaltyCap: 1.0f,
            DirtyLightBoost: 4.0f,
            DirtyLightBaseBoost: 0.5f);

        _priority = new DDGIProbePriority(_tuning);
        _probeSnapshots = new DDGIProbePriority.ProbeSnapshot[_volume.ProbeCount];
        _lightsSeeded = false;
        _tickCounter = 0;
    }

    /// <inheritdoc />
    public void Initialize(IEnginePluginHost host)
    {
        DDGIVolumeRegistry.Register(this, _volume);
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        DDGIVolumeRegistry.Unregister(this);
    }

    /// <inheritdoc />
    public void Dispose() => Shutdown();

    /// <inheritdoc />
    public RendererPluginPlan BuildPlan(RendererPluginContext context)
    {
        _tickCounter++;
        _priority.AdvanceFrame(_tickCounter);

        DDGIProbePriority.LightInfluence[] influences =
            BuildLightInfluences(context.Scene);

        if (influences.Length == 0)
        {
            if (_tickCounter % LogSampleEvery == 0)
            {
                Log.Info(
                    $"[DDGI] tick={_tickCounter} no scene lights, deferring Gi admission",
                    "DDGI");
            }
            return new RendererPluginPlan();
        }

        RefreshProbeSnapshots();

        DDGIProbePriority.CameraSnapshot camera = BuildCameraSnapshot();

        IReadOnlyList<int> updates = _priority.ScheduleProbeUpdates(
            _probeSnapshots,
            influences,
            MaxProbesPerFrame,
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
                $"[DDGI] tick={_tickCounter} volumeProbes={_probeSnapshots.Length} " +
                $"lightsDirty={influences.Length} scheduling={updates.Count} " +
                $"admitted={admittedProbes} deferred={deferredProbes}",
                "DDGI");
        }

        return new RendererPluginPlan();
    }

    private void RefreshProbeSnapshots()
    {
        for (int i = 0; i < _probeSnapshots.Length; ++i)
        {
            _probeSnapshots[i] = new DDGIProbePriority.ProbeSnapshot(
                Index: i,
                Position: _volume.PositionAt(i),
                LastUpdateFrame: _tickCounter - 1);
        }
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

    private static DDGIProbePriority.CameraSnapshot BuildCameraSnapshot()
    {
        // TODO Phase 3: read camera world transform off the active
        // scene-camera entity once RendererPluginContext exposes a
        // CameraPose field on the Renderer's ActiveCameraEntity.
        // Until then we fall back to a stable but trivially-known
        // view pose so distance scoring degrades predictably.
        Vector3 position = Vector3.Zero;
        Vector3 forward = new(0f, 0f, -1f);
        Vector3 up = Vector3.UnitY;
        Vector3 right = Vector3.Cross(up, forward);

        return new DDGIProbePriority.CameraSnapshot(
            Position: position,
            Forward: forward,
            Up: up,
            Right: right,
            FieldOfViewRadians: (float)Math.PI / 3f,
            NearDistance: 0.1f,
            AspectRatio: 16f / 9f);
    }

    private static Vector3 ReadFloat3(float[] source, Vector3 fallback)
    {
        if (source is null || source.Length < 3)
            return fallback;
        return new Vector3(source[0], source[1], source[2]);
    }
}
