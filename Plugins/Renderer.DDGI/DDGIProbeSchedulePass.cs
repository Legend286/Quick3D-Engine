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

/// <summary>Selects the highest-priority active probes entirely on the GPU.</summary>
public sealed class DDGIProbeSchedulePass : RenderPass, IDisposable
{
    public const int PersistentScanWindow = 8192;
    public const int InteractivePersistentScanWindow = 2048;
    public const uint SchedulerThreadCount = 128;
    public const int InteractiveProbeLimit = 24;

    [StructLayout(LayoutKind.Sequential)]
    private struct SchedulePushData
    {
        public Vector4 CameraPositionAndFrame;
        public uint FrameNumber;
        public uint RequestCount;
        public uint PersistentStart;
        public uint PersistentCount;
        public uint AllocatedProbeCount;
        public uint MaxUpdates;
        public uint RadianceRevision;
        public uint GridResolution;
        public uint ClipmapLevelCount;
        public uint Padding0;
        public ulong ProbeRequests;
        public ulong ProbePositions;
        public ulong ProbeStates;
        public ulong ProbeCounter;
        public ulong ProbeUpdateQueue;
    }

    private readonly DDGIAtlasResources _atlas;
    private readonly IActiveCameraDataProvider _cameraProvider;
    private readonly GpuWorkScheduler _gpuWorkScheduler;
    private readonly ISceneGpuDataProvider? _sceneGpuData;
    private readonly RhiShader _shader;
    private readonly RhiPipeline _pipeline;

    public DDGIProbeSchedulePass(
        RhiDevice device,
        string shaderSource,
        DDGIAtlasResources atlas,
        IActiveCameraDataProvider cameraProvider,
        GpuWorkScheduler gpuWorkScheduler,
        ISceneGpuDataProvider? sceneGpuData,
        IReadOnlyList<string>? includeDirs,
        IReadOnlyList<string>? cliArgs,
        ShaderCompileCache? compileCache)
    {
        _atlas = atlas;
        _cameraProvider = cameraProvider;
        _gpuWorkScheduler = gpuWorkScheduler;
        _sceneGpuData = sceneGpuData;
        Name = "DDGI Probe Schedule";
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
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeStates);
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeCounter);
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeUpdateQueue);
        builder.Read(
            _atlas.ResourceHandles.ProbePositions,
            ResourceState.ShaderRead);
        builder.Read(
            _atlas.ResourceHandles.ProbeStates,
            ResourceState.ShaderRead);
        builder.Write(
            _atlas.ResourceHandles.ProbeCounter,
            ResourceState.UnorderedAccess);
        builder.Write(
            _atlas.ResourceHandles.ProbeUpdateQueue,
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
            out _,
            out _);
        _sceneGpuData?.PrepareSceneGpuData(
            context.FrameNumber,
            context.Width,
            context.Height);
        uint lightRevision =
            _sceneGpuData?.CurrentLightRevision ?? 0u;
        uint skyRevision =
            _sceneGpuData?.CurrentSkyRevision ?? 0u;
        uint radianceRevision =
            DDGIAtlasResources.PackRadianceRevision(
                lightRevision,
                skyRevision);
        _atlas.TrackRadianceRevision(
            radianceRevision,
            _atlas.AllocatedProbeCount);
        int updateAllowance = Math.Min(
            _gpuWorkScheduler.GetUnitAllowance(GpuWorkDomain.Gi),
            DDGIProbeUpdatePass.MaxProbesPerFrame);
        if (_atlas.RadianceIsInteractive)
        {
            updateAllowance = Math.Min(
                updateAllowance,
                InteractiveProbeLimit);
        }
        if (!_gpuWorkScheduler.TryAdmit(
                GpuWorkDomain.Gi,
                updateAllowance))
        {
            updateAllowance = 0;
        }
        _atlas.ScheduledProbeCapacity = updateAllowance;
        _atlas.ConsumeRadianceRefreshAllowance(updateAllowance);
        int persistentCount = Math.Min(
            _atlas.AllocatedProbeCount,
            _atlas.RadianceIsInteractive
                ? InteractivePersistentScanWindow
                : PersistentScanWindow);
        int persistentStart = _atlas.GetPersistentScanStart();
        SchedulePushData push = new()
        {
            CameraPositionAndFrame = new Vector4(
                cameraPosition,
                0.0f),
            FrameNumber = (uint)context.FrameNumber,
            RequestCount = (uint)_atlas.RequestCount,
            PersistentStart = (uint)persistentStart,
            PersistentCount = (uint)persistentCount,
            AllocatedProbeCount =
                (uint)_atlas.AllocatedProbeCount,
            MaxUpdates = (uint)updateAllowance,
            RadianceRevision = radianceRevision,
            GridResolution = (uint)_atlas.GridResolution.X,
            ClipmapLevelCount = (uint)_atlas.ClipmapLevelCount,
            ProbeRequests = _atlas.ProbeRequests.DeviceAddress,
            ProbePositions = _atlas.ProbePositions.DeviceAddress,
            ProbeStates = _atlas.ProbeStates.DeviceAddress,
            ProbeCounter = _atlas.ProbeCounter.DeviceAddress,
            ProbeUpdateQueue = _atlas.ProbeUpdateQueue.DeviceAddress
        };
        _atlas.AdvancePersistentScan(persistentCount);
        sink.BeginComputePass(Name);
        sink.BindPipeline(_pipeline);
        sink.UseBuffer(_atlas.ProbeRequests, 1);
        sink.UseBuffer(_atlas.ProbePositions, 1);
        sink.UseBuffer(_atlas.ProbeStates, 1);
        sink.UseBuffer(_atlas.ProbeCounter, 2);
        sink.UseBuffer(_atlas.ProbeUpdateQueue, 2);
        sink.PushConstants(
            0,
            (uint)sizeof(SchedulePushData),
            (IntPtr)(&push));
        sink.Dispatch(
            1,
            1,
            1,
            SchedulerThreadCount,
            1,
            1);
        sink.EndComputePass();
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _shader.Dispose();
    }
}
