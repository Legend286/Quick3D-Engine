// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
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

/// <summary>
/// Rebuilds the sparse DDGI layout on the GPU every frame. The GPU owns probe
/// positions, grid indirection, active count, and indirect debug draw arguments.
/// The CPU supplies only scene AABBs and volume constants.
/// </summary>
public sealed class DDGIProbePlacementPass : RenderPass, IDisposable
{
    public const string PlacementShaderSource = "ddgi_probe_placement.slang";
    public const float ProbeFreeSpaceRadiusMeters = 0.50f;
    public const float ProbeInsideGeometryEpsilon = 0.05f;
    public const int MaxOctreeDepth = 3;
    public const int CoarseGridResolution = 32;
    public const int CoarseGridCells =
        CoarseGridResolution * CoarseGridResolution * CoarseGridResolution;
    public const int MaxProbeBudget = DDGIProbeVolume.DefaultMaxProbesTotalBudget;

    [StructLayout(LayoutKind.Sequential)]
    private struct PlacementPushData
    {
        public Vector4 VolumeMinAndCoarseRes;
        public Vector4 VolumeCellSizeAndMaxDepth;
        public Vector4 ProbeBudgetAndParams;
        public ulong MeshAabbs;
        public ulong ProbePositions;
        public ulong GridToProbeIndex;
        public ulong ProbeCounter;
        public ulong ProbeDrawArgs;
        public uint UseSceneTlas;
        public uint Padding0;
        public uint Padding1;
        public uint Padding2;
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
        _meshAabbCapacity = 1024;
        _meshAabbBuffer = RhiBuffer.Create(
            device,
            (ulong)_meshAabbCapacity * (ulong)Marshal.SizeOf<MeshAabb>(),
            RhiNative.BufferUsage.Storage);
        _meshAabbBuffer.SetDebugName("DDGI Placement Mesh AABBs", "DDGI");

        Name = "DDGI Probe Placement";
        Queue = RhiNative.QueueType.Graphics;
        _shader = compileCache == null
            ? RhiShader.FromSource(
                device, shaderSource, "computeMain",
                RhiNative.ShaderStage.Compute, includeDirs, cliArgs)
            : (RhiShader)compileCache.GetOrCompileHash(
                shaderSource, "computeMain", RhiNative.ShaderStage.Compute,
                includeDirs, cliArgs,
                () => RhiShader.FromSource(
                    device, shaderSource, "computeMain",
                    RhiNative.ShaderStage.Compute, includeDirs, cliArgs));
        _shader.SetDebugName("DDGI Probe Placement CS", "DDGI");
        _pipeline = RhiPipeline.CreateCompute(device, _shader);
        _pipeline.SetDebugName("DDGI Probe Placement Pipeline", "DDGI");
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.ImportBuffer(_atlas.ResourceHandles.ProbePositions);
        builder.ImportBuffer(_atlas.ResourceHandles.GridToProbeIndex);
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeCounter);
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeDrawArgs);
        builder.Write(
            _atlas.ResourceHandles.ProbePositions,
            ResourceState.UnorderedAccess);
        builder.Write(
            _atlas.ResourceHandles.GridToProbeIndex,
            ResourceState.UnorderedAccess);
        builder.Write(
            _atlas.ResourceHandles.ProbeCounter,
            ResourceState.UnorderedAccess);
        builder.Write(
            _atlas.ResourceHandles.ProbeDrawArgs,
            ResourceState.UnorderedAccess);
    }

    public override unsafe void Execute(ICommandSink sink, RenderGraphContext context)
    {
        _atlas.ZeroProbeCounter();

        MeshAabb[] aabbs = CollectMeshAabbs(_world);
        // A missing ECS-side AABB snapshot must not collapse the GPU
        // layout to zero probes. A valid TLAS enables free-space tests;
        // otherwise the shader accepts the volume cells directly.
        EnsureMeshAabbCapacity(aabbs.Length);
        UploadMeshAabbs(aabbs);
        RaytracingSceneCache.TlasUpdateResult tlasInfo;
        try
        {
            tlasInfo = _sceneCache.TryUpdateTlas(sink);
        }
        catch (Exception exception)
        {
            Log.Error(
                $"[DDGI] placement TLAS unavailable; using GPU volume fallback: " +
                $"{exception.Message}",
                "DDGI");
            tlasInfo = default;
        }
        bool useSceneTlas = tlasInfo.SceneTlas != null;

        Vector3 volumeMin = _atlas.Origin - _atlas.Extent;
        Vector3 cellSize = _atlas.Extent * 2.0f / CoarseGridResolution;
        PlacementPushData push = new()
        {
            VolumeMinAndCoarseRes = new Vector4(
                volumeMin, CoarseGridResolution),
            VolumeCellSizeAndMaxDepth = new Vector4(
                cellSize, MaxOctreeDepth),
            ProbeBudgetAndParams = new Vector4(
                MaxProbeBudget,
                ProbeFreeSpaceRadiusMeters,
                ProbeInsideGeometryEpsilon,
                aabbs.Length),
            MeshAabbs = _meshAabbBuffer.DeviceAddress,
            ProbePositions = _atlas.ProbePositions.DeviceAddress,
            GridToProbeIndex = _atlas.GridToProbeIndex.DeviceAddress,
            ProbeCounter = _atlas.ProbeCounter.DeviceAddress,
            ProbeDrawArgs = _atlas.ProbeDrawArgs.DeviceAddress,
            UseSceneTlas = useSceneTlas ? 1u : 0u,
        };

        sink.BeginComputePass(Name);
        sink.BindPipeline(_pipeline);
        sink.UseBuffer(_meshAabbBuffer, 1);
        sink.UseBuffer(            _atlas.ProbePositions, 2);

        sink.UseBuffer(_atlas.GridToProbeIndex, 2);
        sink.UseBuffer(_atlas.ProbeCounter, 2);
        sink.UseBuffer(_atlas.ProbeDrawArgs, 2);
        if (useSceneTlas)
        {
            sink.BindAccelStruct(3, tlasInfo.SceneTlas!);
            sink.UseAccelStruct(tlasInfo.SceneTlas!, 1);
        }
        sink.PushConstants(0, (uint)sizeof(PlacementPushData), (IntPtr)(&push));
        sink.Dispatch(
            CoarseGridResolution / 8u,
            CoarseGridResolution / 8u,
            CoarseGridResolution / 8u,
            8, 8, 8);
        sink.EndComputePass();
        _atlas.MarkSparseLayoutReady();
    }

    private void ClearEmptySparseLayout()
    {
        int[] grid = new int[_atlas.CoarseGridCells];
        Array.Fill(grid, -1);
        _atlas.GridToProbeIndex.Upload(grid);
        _atlas.ZeroProbeCounter();
        _atlas.MarkSparseLayoutReady();
    }

    private static MeshAabb[] CollectMeshAabbs(IEntityStore world)
    {
        var result = new List<MeshAabb>();
        foreach (ulong entityId in world.Entities)
        {
            if (!world.TryGet<ModelComponent>(entityId, out ModelComponent modelComp))
                continue;
            Transform transform = world.TryGet<Transform>(entityId, out Transform value)
                ? value
                : Transform.Default;
            Model? model = AssetRegistry.GetModel(modelComp.ModelId);
            if (model?.Parts == null)
                continue;

            foreach (ModelPart part in model.Parts)
            {
                Matrix4x4 modelMatrix =
                    Matrix4x4.CreateScale(transform.Scale) *
                    Matrix4x4.CreateFromQuaternion(transform.Rotation) *
                    Matrix4x4.CreateTranslation(transform.Position);
                Matrix4x4 fullMatrix =
                    Matrix4x4.CreateTranslation(part.LocalOffset) * modelMatrix;
                TransformBoundingBox(
                    part.BoundsMin,
                    part.BoundsMax,
                    fullMatrix,
                    out Vector3 worldMin,
                    out Vector3 worldMax);
                Vector3 grow = (worldMax - worldMin) * 0.05f + new Vector3(0.10f);
                result.Add(new MeshAabb
                {
                    Min = new Vector4(worldMin - grow, 0f),
                    Max = new Vector4(worldMax + grow, 0f),
                });
            }
        }
        return result.ToArray();
    }

    private static void TransformBoundingBox(
        Vector3 localMin,
        Vector3 localMax,
        Matrix4x4 matrix,
        out Vector3 worldMin,
        out Vector3 worldMax)
    {
        Span<Vector3> corners = stackalloc Vector3[8];
        int index = 0;
        for (int z = 0; z < 2; ++z)
        for (int y = 0; y < 2; ++y)
        for (int x = 0; x < 2; ++x)
        {
            corners[index++] = Vector3.Transform(
                new Vector3(
                    x == 0 ? localMin.X : localMax.X,
                    y == 0 ? localMin.Y : localMax.Y,
                    z == 0 ? localMin.Z : localMax.Z),
                matrix);
        }

        worldMin = corners[0];
        worldMax = corners[0];
        for (int i = 1; i < corners.Length; ++i)
        {
            worldMin = Vector3.Min(worldMin, corners[i]);
            worldMax = Vector3.Max(worldMax, corners[i]);
        }
    }

    private void EnsureMeshAabbCapacity(int required)
    {
        if (required <= _meshAabbCapacity)
            return;
        int capacity = _meshAabbCapacity;
        while (capacity < required)
            capacity *= 2;
        _meshAabbBuffer.Dispose();
        _meshAabbCapacity = capacity;
        _meshAabbBuffer = RhiBuffer.Create(
            _device,
            (ulong)capacity * (ulong)Marshal.SizeOf<MeshAabb>(),
            RhiNative.BufferUsage.Storage);
        _meshAabbBuffer.SetDebugName("DDGI Placement Mesh AABBs", "DDGI");
    }

    private unsafe void UploadMeshAabbs(MeshAabb[] aabbs)
    {
        fixed (MeshAabb* data = aabbs)
        {
            _meshAabbBuffer.Upload(new ReadOnlySpan<byte>(
                data,
                aabbs.Length * Marshal.SizeOf<MeshAabb>()));
        }
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _shader.Dispose();
        _meshAabbBuffer.Dispose();
        _sceneCache.Dispose();
    }
}
