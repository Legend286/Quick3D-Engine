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
    private readonly DDGILightTreeBuilder _lightTreeBuilder = new();
    private DDGIProbePriority.ProbeSnapshot[] _probeSnapshots =
        Array.Empty<DDGIProbePriority.ProbeSnapshot>();
    private DDGILightSnapshot[] _currentLightSnapshot =
        Array.Empty<DDGILightSnapshot>();
    private bool _lightsSeeded;
    private long _tickCounter;
    private long _lastPlacementTick = -1;
    private long _sceneFingerprint;

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

        long fingerprint = ComputeSceneFingerprint(context);
        if (fingerprint != _sceneFingerprint)
        {
            _sceneFingerprint = fingerprint;
            _lastPlacementTick = -1;
            if (_atlas != null) _atlas.ResetSparseLayoutForSceneReload();
            if (_tickCounter % MaxLogSampleEvery == 0)
            {
                Log.Info(
                    $"[DDGI] scene fingerprint changed; re-running GPU probe placement next tick",
                    "DDGI");
            }
        }

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

        DDGIProbePriority.LightInfluence[] influences =
            BuildLightInfluences(context.Scene);

        UploadLightsSnapshot(context.Scene);
        BuildAndUploadLightTree();

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
        // ScheduleMax constant-bounded probe count without consulting
        // the CPU volume: the probe-update kernel reads positions
        // straight from _atlas.ProbePositions / GridToProbeIndex,
        // so per-tick CPU positioning is unnecessary. Capping at
        // MaxProbesPerFrame gives the scheduler a fixed input shape
        // regardless of whether the GPU placement kernel has finished
        // populating the sparse layout — unallocated cells just
        // return gridToProbeIndex == -1 in the shader.
        int cap = DDGIProbeUpdatePass.MaxProbesPerFrame;
        if (_probeSnapshots.Length != cap)
            _probeSnapshots =
                new DDGIProbePriority.ProbeSnapshot[cap];
        for (int i = 0; i < cap; ++i)
        {
            _probeSnapshots[i] = new DDGIProbePriority.ProbeSnapshot(
                Index: i,
                Position: Vector3.Zero,
                LastUpdateFrame: _tickCounter - 1);
        }
    }

    private bool ShouldKickPlacement()
    {
        if (_atlas == null) return false;
        if (_lastPlacementTick > 0) return false;
        return true;
    }

    private long ComputeSceneFingerprint(RendererPluginContext context)
    {
        int lights = context.Scene?.Lights?.Count ?? 0;
        int entities = 0;
        if (context.World != null)
        {
            foreach (int _ in context.World.Entities) entities++;
        }
        int contentHash = context.ContentRoot == null
            ? 0
            : context.ContentRoot.GetHashCode();
        long combined = (long)(uint)lights;
        unchecked
        {
            combined ^= (long)entities << 16;
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

        var snapshot = new DDGILightSnapshot[capacity];
        if (scene?.Lights != null)
        {
            int count = Math.Min(scene.Lights.Count, capacity);
            for (int i = 0; i < count; ++i)
            {
                LightNode node = scene.Lights[i];
                Vector3 position = ReadFloat3(node.Position, Vector3.Zero);
                Vector3 color = ReadFloat3(node.Color, Vector3.One);
                float range = node.Range > 0f ? node.Range : 1.0e6f;
                float intensity = node.Intensity > 0f ? node.Intensity : 0f;

                float type = string.Equals(node.Type, "point",
                    StringComparison.OrdinalIgnoreCase) ? 1f
                    : string.Equals(node.Type, "spot",
                        StringComparison.OrdinalIgnoreCase) ? 2f
                    : 0f;

                Vector3 axis = type != 0f
                    ? Vector3.Normalize(Vector3.Zero - position)
                    : -ReadFloat3(node.Direction, new Vector3(0, -1, 0));

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
        }

        _atlas.UploadLights(snapshot);
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
    private const int SparseLayoutWarmupTicks = 3;

    /// <summary>Per-probe cap on the number of light-tree leaves
    /// visited by the probe-update kernel's hierarchical traversal.
    /// Cross-file consumers (like <see cref="DDGIProbeUpdatePass"/>)
    /// pull this from the push struct; bump it together with the
    /// shader's <c>kWorklistCap</c> if tree depth grows past the
    /// current frontier headroom.</summary>
    public const int LeafVisitBudget = 4;
}

