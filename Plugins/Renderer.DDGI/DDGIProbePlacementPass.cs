// SPDX-License-Identifier: MIT
// GPU-only DDGI sparse probe placement pipeline:
//   1. Reads scene mesh AABBs from the raster-scene instance buffer.
//   2. Walks a top-down octree over the volume's coarse 32³ grid,
//      subdividing only cells whose AABB intersects any mesh AABB.
//   3. For each leaf at the maximum octree depth, casts six outward
//      rays (±X, ±Y, ±Z) at the host TLAS and rejects candidates
//      that fail the inside-geometry (ε=5 cm) or free-space (r=50 cm)
//      sanity checks.
//   4. Atomic-allocates slot positions in the ProbePositions SSBO
//      and writes the coarse-grid indirection table.
// Consumed by the DDGIRendererPlugin once per scene-load; subsequent
// ticks the plugin skips the pass and lets the probe-update pass
// re-radiance the existing sparse positions.

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.Assets;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.RenderGraph.Shaders;
using Engine.Scene;
using Engine.Scene.Components;

namespace Engine.DDGI;

public sealed class DDGIProbePlacementPass : RenderPass, IDisposable
{
    public const string PlacementShaderSource = "shaders/ddgi_probe_placement.slang";

    public const float ProbeFreeSpaceRadiusMeters = 0.50f;
    public const float ProbeInsideGeometryEpsilon = 0.05f;
    public const int MaxOctreeDepth = 3;
    public const int CoarseGridResolution = 32;
    public const int CoarseGridCells = CoarseGridResolution *
        CoarseGridResolution * CoarseGridResolution;
    public const int MaxProbeBudget = 4096;

    [StructLayout(LayoutKind.Sequential)]
    private struct PlacementPushData
    {
        public Vector4 VolumeMinAndCoarseRes;
        public Vector4 VolumeCellSizeAndMaxDepth;
        public Vector4 ProbeBudgetAndParams; // x:maxProbes y:freeSpaceR z:insideEps w:meshAabbCount
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshAabb
    {
        public Vector4 Min;
        public Vector4 Max;
    }

    private readonly RhiDevice _device;
    private readonly IEntityStore _world;
    private readonly DDGIAtlasResources _atlas;
    private readonly RaytracingSceneCache _sceneCache;
    private readonly RhiShader _shader;
    private readonly RhiPipeline _pipeline;
    private RhiBuffer _meshAabbBuffer;
    private int _meshAabbCapacity;
    private readonly IReadOnlyList<string>? _includeDirs;
    private readonly IReadOnlyList<string>? _cliArgs;
    private readonly ShaderCompileCache? _compileCache;

    public DDGIProbePlacementPass(
        RhiDevice device,
        IEntityStore world,
        string shaderSource,
        DDGIAtlasResources atlas,
        IReadOnlyList<string>? includeDirs,
        IReadOnlyList<string>? cliArgs,
        ShaderCompileCache? compileCache = null)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (atlas == null) throw new ArgumentNullException(nameof(atlas));

        _device = device;
        _world = world;
        _atlas = atlas;
        _sceneCache = new RaytracingSceneCache(device, world);
        _includeDirs = includeDirs;
        _cliArgs = cliArgs;
        _compileCache = compileCache;

        _meshAabbCapacity = 1024;
        _meshAabbBuffer = RhiBuffer.Create(
            device,
            (ulong)_meshAabbCapacity * (ulong)Marshal.SizeOf<MeshAabb>(),
            RhiNative.BufferUsage.Storage);
        _meshAabbBuffer.SetDebugName(
            "DDGI Placement Mesh AABBs", "DDGI");

        Name = "DDGI Probe Placement";
        Queue = RhiNative.QueueType.Compute;

        if (compileCache == null)
        {
            _shader = RhiShader.FromSource(
                device, shaderSource, "computeMain",
                RhiNative.ShaderStage.Compute,
                includeDirs, cliArgs);
        }
        else
        {
            _shader = (RhiShader)compileCache.GetOrCompileHash(
                shaderSource, "computeMain",
                RhiNative.ShaderStage.Compute,
                includeDirs, cliArgs,
                () => RhiShader.FromSource(
                    device, shaderSource, "computeMain",
                    RhiNative.ShaderStage.Compute,
                    includeDirs, cliArgs));
        }
        _shader.SetDebugName("DDGI Probe Placement CS", "DDGI");

        _pipeline = RhiPipeline.CreateCompute(_device, _shader);
        _pipeline.SetDebugName(
            "DDGI Probe Placement Pipeline", "DDGI");
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Write(RenderGraphResources.BackBufferHandle, ResourceState.UnorderedAccess);
    }

    public override unsafe void Execute(ICommandSink sink, RenderGraphContext context)
    {
        if (_atlas == null) return;

        _atlas.ZeroProbeCounter();

        MeshAabb[] aabbs = CollectMeshAabbs(_world);
        if (aabbs.Length == 0)
        {
            UploadEmptySparseLayout();
            return;
        }
        EnsureMeshAabbCapacity(aabbs.Length);
        UploadMeshAabbs(aabbs);

        RaytracingSceneCache.TlasUpdateResult tlasInfo =
            _sceneCache.TryUpdateTlas(sink);
        if (tlasInfo.SceneTlas == null)
        {
            UploadEmptySparseLayout();
            return;
        }

        Vector3 volumeMin = _atlas.Origin - _atlas.Extent;
        Vector3 volumeCellSize =
            _atlas.Extent * 2.0f / CoarseGridResolution;

        PlacementPushData push = new()
        {
            VolumeMinAndCoarseRes = new Vector4(
                volumeMin.X, volumeMin.Y, volumeMin.Z,
                (float)CoarseGridResolution),
            VolumeCellSizeAndMaxDepth = new Vector4(
                volumeCellSize.X,
                volumeCellSize.Y,
                volumeCellSize.Z,
                (float)MaxOctreeDepth),
            ProbeBudgetAndParams = new Vector4(
                (float)MaxProbeBudget,
                ProbeFreeSpaceRadiusMeters,
                ProbeInsideGeometryEpsilon,
                (float)aabbs.Length),
        };

        sink.BeginComputePass(Name);
        sink.BindPipeline(_pipeline);
        sink.UseBuffer(_meshAabbBuffer, 1);
        sink.UseBuffer(_atlas.ProbePositions, 5);
        sink.UseBuffer(_atlas.GridToProbeIndex, 6);
        sink.UseBuffer(_atlas.ProbeCounter, 0);

        if (_atlas.SharedHeap != null && _atlas.SharedHeap.IsInitialized)
            sink.BindHeap(1, _atlas.SharedHeap);

        sink.BindAccelStruct(3, tlasInfo.SceneTlas);
        sink.UseAccelStruct(tlasInfo.SceneTlas, 1);

        sink.PushConstants(
            0,
            (uint)sizeof(PlacementPushData),
            (IntPtr)(&push));

        sink.Dispatch((uint)CoarseGridResolution,
            (uint)CoarseGridResolution,
            (uint)CoarseGridResolution,
            1, 1, 1);
        sink.EndComputePass();
    }

    private void UploadEmptySparseLayout()
    {
        int coarseCells = _atlas.CoarseGridCells;
        var positions = Array.Empty<Vector3>();
        var grid = new int[coarseCells];
        Array.Fill(grid, -1);
        _atlas.UploadSparseLayout(positions, grid);
    }

    private static MeshAabb[] CollectMeshAabbs(IEntityStore world)
    {
        var result = new List<MeshAabb>();
        foreach (var entityId in world.Entities)
        {
            if (!world.TryGet<ModelComponent>(entityId, out var modelComp))
                continue;
            Engine.Scene.Components.Transform transform =
                world.TryGet<Engine.Scene.Components.Transform>(entityId, out var t)
                ? t
                : Engine.Scene.Components.Transform.Default;
            var model = AssetRegistry.GetModel(modelComp.ModelId);
            if (model == null || model.Parts == null) continue;
            foreach (var part in model.Parts)
            {
                Vector3 localMin = part.BoundsMin;
                Vector3 localMax = part.BoundsMax;
                Matrix4x4 partMat =
                    Matrix4x4.CreateTranslation(part.LocalOffset);
                Matrix4x4 modelMat =
                    Matrix4x4.CreateScale(transform.Scale) *
                    Matrix4x4.CreateFromQuaternion(transform.Rotation) *
                    Matrix4x4.CreateTranslation(transform.Position);
                Matrix4x4 fullMat = partMat * modelMat;
                TransformBoundingBox(
                    localMin, localMax, fullMat,
                    out Vector3 worldMin, out Vector3 worldMax);
                AddExpandedAabb(result, worldMin, worldMax);
            }
        }
        return result.ToArray();
    }

    private static void AddExpandedAabb(
        List<MeshAabb> result, Vector3 min, Vector3 max)
    {
        Vector3 grow = new(
            (max.X - min.X) * 0.05f + 0.10f,
            (max.Y - min.Y) * 0.05f + 0.10f,
            (max.Z - min.Z) * 0.05f + 0.10f);
        result.Add(new MeshAabb
        {
            Min = new Vector4(min - grow, 0f),
            Max = new Vector4(max + grow, 0f),
        });
    }

    private static void TransformBoundingBox(
        Vector3 localMin, Vector3 localMax,
        Matrix4x4 matrix,
        out Vector3 worldMin, out Vector3 worldMax)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        int idx = 0;
        for (int oz = 0; oz < 2; ++oz)
        for (int oy = 0; oy < 2; ++oy)
        for (int ox = 0; ox < 2; ++ox)
        {
            Vector3 corner = new(
                ox == 0 ? localMin.X : localMax.X,
                oy == 0 ? localMin.Y : localMax.Y,
                oz == 0 ? localMin.Z : localMax.Z);
            corners[idx++] = Vector3.Transform(corner, matrix);
        }
        worldMin = corners[0];
        worldMax = corners[0];
        for (int i = 1; i < 8; ++i)
        {
            worldMin = Vector3.Min(worldMin, corners[i]);
            worldMax = Vector3.Max(worldMax, corners[i]);
        }
    }

    private void EnsureMeshAabbCapacity(int required)
    {
        if (required <= _meshAabbCapacity) return;
        int newCap = Math.Max(_meshAabbCapacity, 1024);
        while (newCap < required)
            newCap *= 2;
        _meshAabbBuffer?.Dispose();
        _meshAabbCapacity = newCap;
        _meshAabbBuffer = RhiBuffer.Create(
            _device,
            (ulong)_meshAabbCapacity * (ulong)Marshal.SizeOf<MeshAabb>(),
            RhiNative.BufferUsage.Storage);
        _meshAabbBuffer.SetDebugName(
            "DDGI Placement Mesh AABBs", "DDGI");
    }

    private unsafe void UploadMeshAabbs(MeshAabb[] aabbs)
    {
        int structSize = Marshal.SizeOf<MeshAabb>();
        fixed (MeshAabb* p = aabbs)
        {
            _meshAabbBuffer.Upload(new ReadOnlySpan<byte>(
                p, aabbs.Length * structSize));
        }
    }

    public void Dispose()
    {
        _pipeline?.Dispose();
        _shader?.Dispose();
        _meshAabbBuffer?.Dispose();
        _sceneCache?.Dispose();
    }
}
