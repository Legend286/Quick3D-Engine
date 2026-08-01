// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.CBindings;
using Engine.Plugins;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.RenderGraph.Shaders;

namespace Engine.DDGI;

/// <summary>Scrolls and classifies the camera-relative DDGI cache on the GPU.</summary>
public sealed class DDGIProbePlacementPass : RenderPass, IDisposable
{
    public const string PlacementShaderSource =
        "ddgi_probe_placement.slang";
    public const float ProbeFreeSpaceRadiusMeters = 0.50f;
    public const float ProbeInsideGeometryEpsilon = 0.05f;
    public const int MaxRelocationClassificationsPerFrame = 128;

    [StructLayout(LayoutKind.Sequential)]
    private struct PlacementPushData
    {
        public Matrix4x4 ViewProjection;
        public Vector4 CameraPositionAndFrame;
        public Vector4 VolumeExtentAndGridResolution;
        public Vector4 ProbeBudgetAndParams;
        public ulong ProbeRequests;
        public ulong ProbePositions;
        public ulong GridToProbeIndex;
        public ulong ProbeCounter;
        public ulong ProbeStates;
        public ulong VolumeState;
        public ulong ProbeWorldKeys;
        public ulong WorldProbeHash;
        public ulong Instances;
        public ulong Parts;
        public uint RequestCount;
        public uint UseSceneTlas;
        public uint MaxRelocationClassifications;
        public uint MaxSceneBakeClassifications;
        public uint GeometryRevision;
        public uint ClipmapLevelCount;
        public uint WorldProbeHashCapacity;
        public uint InstanceCount;
        public uint PartCount;
    }

    private readonly DDGIAtlasResources _atlas;
    private readonly IActiveCameraDataProvider _cameraProvider;
    private readonly RaytracingSceneCache _sceneCache;
    private readonly GpuWorkScheduler _gpuWorkScheduler;
    private readonly ISceneGpuDataProvider? _sceneGpuData;
    private readonly RhiShader _shader;
    private readonly RhiPipeline _pipeline;

    public DDGIProbePlacementPass(
        RhiDevice device,
        RaytracingSceneCache sceneCache,
        string shaderSource,
        DDGIAtlasResources atlas,
        IActiveCameraDataProvider cameraProvider,
        GpuWorkScheduler gpuWorkScheduler,
        ISceneGpuDataProvider? sceneGpuData,
        IReadOnlyList<string>? includeDirs,
        IReadOnlyList<string>? cliArgs,
        ShaderCompileCache? compileCache = null)
    {
        _atlas = atlas;
        _cameraProvider = cameraProvider;
        _sceneCache = sceneCache;
        _gpuWorkScheduler = gpuWorkScheduler;
        _sceneGpuData = sceneGpuData;
        Name = "DDGI Probe Placement";
        Queue = RhiNative.QueueType.Graphics;
        _shader = compileCache == null
            ? RhiShader.FromSource(
                device,
                shaderSource,
                "computeMain",
                RhiNative.ShaderStage.Compute,
                includeDirs,
                cliArgs)
            : (RhiShader)compileCache.GetOrCompileHash(
                shaderSource,
                "computeMain",
                RhiNative.ShaderStage.Compute,
                includeDirs,
                cliArgs,
                () => RhiShader.FromSource(
                    device,
                    shaderSource,
                    "computeMain",
                    RhiNative.ShaderStage.Compute,
                    includeDirs,
                    cliArgs));
        _pipeline = RhiPipeline.CreateCompute(device, _shader);
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.ImportBuffer(_atlas.ResourceHandles.ProbePositions);
        builder.ImportBuffer(_atlas.ResourceHandles.GridToProbeIndex);
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeCounter);
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeStates);
        builder.ImportBuffer(_atlas.ResourceHandles.VolumeState);
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeWorldKeys);
        builder.ImportBuffer(_atlas.ResourceHandles.WorldProbeHash);
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
            _atlas.ResourceHandles.ProbeStates,
            ResourceState.UnorderedAccess);
        builder.Write(
            _atlas.ResourceHandles.VolumeState,
            ResourceState.UnorderedAccess);
        builder.Write(
            _atlas.ResourceHandles.ProbeWorldKeys,
            ResourceState.UnorderedAccess);
        builder.Write(
            _atlas.ResourceHandles.WorldProbeHash,
            ResourceState.UnorderedAccess);
    }

    public override unsafe void Execute(
        ICommandSink sink,
        RenderGraphContext context)
    {
        _cameraProvider.TryGetViewportCameraData(
            context.Width,
            context.Height,
            out Vector3 cameraPosition,
            out Matrix4x4 viewProjection,
            out _);

        _sceneGpuData?.PrepareSceneGpuData(
            context.FrameNumber,
            context.Width,
            context.Height);
        RhiBuffer? instanceBuffer =
            _sceneGpuData?.CurrentInstanceBuffer;
        RhiBuffer? partBuffer =
            _sceneGpuData?.CurrentPartBuffer;
        Vector3 sceneBoundsMin = Vector3.Zero;
        Vector3 sceneBoundsMax = Vector3.Zero;
        bool hasSceneBounds = _sceneGpuData != null &&
            _sceneGpuData.TryGetSceneBounds(
                out sceneBoundsMin,
                out sceneBoundsMax);
        uint geometryRevision =
            _sceneGpuData?.CurrentGeometryRevision ?? 0u;
        int updateAllowance = Math.Min(
            _gpuWorkScheduler.GetUnitAllowance(GpuWorkDomain.Gi),
            DDGIProbeUpdatePass.MaxProbesPerFrame);
        int sceneBakeRequestBudget =
            CalculateSceneBakeRequestBudget(updateAllowance);
        RaytracingSceneCache.TlasUpdateResult tlasInfo;
        try
        {
            tlasInfo = _sceneCache.TryUpdateTlas(
                sink,
                context.FrameNumber);
        }
        catch
        {
            tlasInfo = default;
        }
        _atlas.WorldCache.PrepareFrame(
            cameraPosition,
            geometryRevision,
            hasSceneBounds,
            sceneBoundsMin,
            sceneBoundsMax,
            sceneBakeRequestBudget,
            canClassifySceneBake: tlasInfo.SceneTlas != null);
        _atlas.SelectRequestBuffer(context.FrameNumber);
        _atlas.ProbeRequests.Upload(_atlas.WorldCache.Requests);

        uint gridResolution =
            (uint)_atlas.GridResolution.X;
        PlacementPushData push = new()
        {
            ViewProjection = viewProjection,
            CameraPositionAndFrame = new Vector4(
                cameraPosition,
                (float)context.FrameNumber),
            VolumeExtentAndGridResolution = new Vector4(
                _atlas.BaseCellSize,
                gridResolution),
            ProbeBudgetAndParams = new Vector4(
                _atlas.MaxProbesTotalBudget,
                ProbeFreeSpaceRadiusMeters,
                ProbeInsideGeometryEpsilon,
                _atlas.ClipmapScale),
            ProbeRequests = _atlas.ProbeRequests.DeviceAddress,
            ProbePositions = _atlas.ProbePositions.DeviceAddress,
            GridToProbeIndex = _atlas.GridToProbeIndex.DeviceAddress,
            ProbeCounter = _atlas.ProbeCounter.DeviceAddress,
            ProbeStates = _atlas.ProbeStates.DeviceAddress,
            VolumeState = _atlas.VolumeState.DeviceAddress,
            ProbeWorldKeys = _atlas.ProbeWorldKeys.DeviceAddress,
            WorldProbeHash = _atlas.WorldProbeHash.DeviceAddress,
            Instances = instanceBuffer?.DeviceAddress ?? 0ul,
            Parts = partBuffer?.DeviceAddress ?? 0ul,
            RequestCount = (uint)_atlas.RequestCount,
            UseSceneTlas = tlasInfo.SceneTlas != null ? 1u : 0u,
            MaxRelocationClassifications =
                MaxRelocationClassificationsPerFrame,
            MaxSceneBakeClassifications =
                (uint)sceneBakeRequestBudget,
            GeometryRevision = geometryRevision,
            ClipmapLevelCount = (uint)_atlas.ClipmapLevelCount,
            WorldProbeHashCapacity =
                (uint)_atlas.WorldProbeHashCapacity,
            InstanceCount =
                _sceneGpuData?.CurrentInstanceCount ?? 0u,
            PartCount = _sceneGpuData?.CurrentPartCount ?? 0u
        };

        sink.BeginComputePass(Name);
        sink.BindPipeline(_pipeline);
        sink.UseBuffer(_atlas.ProbeRequests, 1);
        sink.UseBuffer(_atlas.ProbePositions, 3);
        sink.UseBuffer(_atlas.GridToProbeIndex, 3);
        sink.UseBuffer(_atlas.ProbeCounter, 3);
        sink.UseBuffer(_atlas.ProbeStates, 3);
        sink.UseBuffer(_atlas.VolumeState, 3);
        sink.UseBuffer(_atlas.ProbeWorldKeys, 3);
        sink.UseBuffer(_atlas.WorldProbeHash, 3);
        if (instanceBuffer != null)
            sink.UseBuffer(instanceBuffer, 1);
        if (partBuffer != null)
            sink.UseBuffer(partBuffer, 1);
        if (tlasInfo.SceneTlas != null)
        {
            sink.BindAccelStruct(3, tlasInfo.SceneTlas);
            sink.UseAccelStruct(tlasInfo.SceneTlas, 1);
        }
        sink.PushConstants(
            0,
            (uint)sizeof(PlacementPushData),
            (IntPtr)(&push));
        uint groups = ((uint)_atlas.RequestCount + 63u) / 64u;
        sink.Dispatch(groups, 1, 1, 64, 1, 1);
        sink.EndComputePass();
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _shader.Dispose();
    }

    internal static int CalculateSceneBakeRequestBudget(
        int updateAllowance)
        => updateAllowance <= 0
            ? 0
            : Math.Clamp(
                Math.Max(updateAllowance / 2, 1),
                DDGIWorldProbeCache.MinimumSceneBakeRequestsPerFrame,
                DDGIWorldProbeCache.MaxSceneBakeRequestsPerFrame);
}
