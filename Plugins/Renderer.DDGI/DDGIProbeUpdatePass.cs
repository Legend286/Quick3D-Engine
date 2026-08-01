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
using Engine.Scene;

namespace Engine.DDGI;

/// <summary>Traces and updates the GPU-scheduled DDGI probe queue.</summary>
public sealed class DDGIProbeUpdatePass :
    RenderPass,
    IGpuWorkTimingSource,
    IDisposable
{
    private const int SubmissionHistoryCapacity = 16;
    public const int MaxProbesPerFrame = 128;
    public const int RaysPerProbe = 32;
    public const float MaxTraceDistanceMeters = 64.0f;

    private readonly RhiShader _computeShader;
    private readonly RhiPipeline _pipeline;
    private readonly RhiSampler _sampler;
    private readonly DDGIAtlasResources _atlas;
    private readonly RaytracingSceneCache _sceneCache;
    private readonly IActiveCameraDataProvider _cameraProvider;
    private readonly ISceneGpuDataProvider? _sceneGpuData;
    private readonly long[] _submissionFrames = new long[
        SubmissionHistoryCapacity];
    private readonly int[] _submissionCounts = new int[
        SubmissionHistoryCapacity];
    private readonly bool[] _submissionTimingEligible = new bool[
        SubmissionHistoryCapacity];

    /// <inheritdoc />
    public GpuWorkDomain WorkDomain => GpuWorkDomain.Gi;

    [StructLayout(LayoutKind.Sequential)]
    private struct UpdatePushData
    {
        public uint FrameNumber;
        public uint LightCount;
        public uint RaysPerProbe;
        public uint MaxUpdates;
        public Vector4 CameraPositionAndJitter;
        public Vector4 AtlasUVParams;
        public Vector4 OriginAndProbeCountZ;
        public Vector4 Extent;
        public Vector4 SkySunDirectionAndRadius;
        public Vector4 SkyAtmosphereParameters;
        public uint MaxProbeBudget;
        public uint UseSceneTlas;
        public uint RadianceRevision;
        public float MinimumProbeClearance;
        public ulong ProbePositions;
        public ulong Lights;
        public ulong ProbeCounter;
        public ulong ProbeStates;
        public ulong ProbeUpdateQueue;
        public ulong Instances;
        public ulong Parts;
        public ulong Materials;
        public uint InstanceCount;
        public uint PartCount;
        public uint MaterialCount;
        public uint Padding0;
    }

    public DDGIProbeUpdatePass(
        RhiDevice device,
        RaytracingSceneCache sceneCache,
        string shaderSource,
        IReadOnlyList<string>? includeDirs,
        IReadOnlyList<string>? cliArgs,
        DDGIAtlasResources atlas,
        IActiveCameraDataProvider cameraProvider,
        ISceneGpuDataProvider? sceneGpuData,
        ShaderCompileCache? compileCache = null)
    {
        Array.Fill(_submissionFrames, -1L);
        if (sceneCache == null)
            throw new ArgumentNullException(nameof(sceneCache));
        if (atlas == null) throw new ArgumentNullException(nameof(atlas));

        _atlas = atlas;
        _cameraProvider = cameraProvider;
        _sceneGpuData = sceneGpuData;
        _sceneCache = sceneCache;
        Name = "DDGI Probe Update";
        Queue = RhiNative.QueueType.Graphics;

        _computeShader = compileCache == null
            ? RhiShader.FromSource(
                device, shaderSource, "computeMain",
                RhiNative.ShaderStage.Compute, includeDirs, cliArgs)
            : (RhiShader)compileCache.GetOrCompileHash(
                shaderSource, "computeMain", RhiNative.ShaderStage.Compute,
                includeDirs, cliArgs,
                () => RhiShader.FromSource(
                    device, shaderSource, "computeMain",
                    RhiNative.ShaderStage.Compute, includeDirs, cliArgs));
        _computeShader.SetDebugName("DDGI Probe Update CS", "DDGI");
        _pipeline = RhiPipeline.CreateCompute(device, _computeShader);
        _pipeline.SetDebugName("DDGI Probe Update Pipeline", "DDGI");
        _sampler = RhiSampler.Create(device);
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.ImportBuffer(_atlas.ResourceHandles.ProbePositions);
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeCounter);
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeStates);
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeUpdateQueue);
        builder.ImportTexture(_atlas.ResourceHandles.Irradiance);
        builder.ImportTexture(_atlas.ResourceHandles.Visibility);
        builder.Read(
            _atlas.ResourceHandles.ProbePositions,
            ResourceState.ShaderRead);
        builder.Read(
            _atlas.ResourceHandles.ProbeCounter,
            ResourceState.ShaderRead);
        builder.Read(
            _atlas.ResourceHandles.ProbeUpdateQueue,
            ResourceState.ShaderRead);
        builder.Write(
            _atlas.ResourceHandles.ProbeStates,
            ResourceState.UnorderedAccess);
        builder.Write(
            _atlas.ResourceHandles.Irradiance,
            ResourceState.UnorderedAccess);
        builder.Write(
            _atlas.ResourceHandles.Visibility,
            ResourceState.UnorderedAccess);
    }

    public override unsafe void Execute(
        ICommandSink sink,
        RenderGraphContext context)
    {
        int admittedCount = Math.Clamp(
            _atlas.ScheduledProbeCapacity,
            0,
            MaxProbesPerFrame);
        int submissionSlot = (int)(
            context.FrameNumber % SubmissionHistoryCapacity);
        _submissionFrames[submissionSlot] = context.FrameNumber;
        _submissionCounts[submissionSlot] = admittedCount;
        _submissionTimingEligible[submissionSlot] =
            _atlas.HasBudgetTrainingWork;
        if (admittedCount == 0)
            return;

        RaytracingSceneCache.TlasUpdateResult tlasInfo;
        try
        {
            tlasInfo = _sceneCache.TryUpdateTlas(
                sink,
                context.FrameNumber);
        }
        catch (Exception exception)
        {
            Log.Error(
                $"[DDGI] update TLAS unavailable; using sky fallback: " +
                $"{exception.Message}",
                "DDGI");
            tlasInfo = default;
        }
        bool useSceneTlas = tlasInfo.SceneTlas != null;
        _sceneGpuData?.PrepareSceneGpuData(
            context.FrameNumber,
            context.Width,
            context.Height);
        RhiBuffer? lightBuffer = _sceneGpuData?.CurrentLightBuffer;
        RhiBuffer? instanceBuffer = _sceneGpuData?.CurrentInstanceBuffer;
        RhiBuffer? partBuffer = _sceneGpuData?.CurrentPartBuffer;
        RhiBuffer? materialBuffer = _sceneGpuData?.CurrentMaterialBuffer;
        uint lightCount = _sceneGpuData?.CurrentLightCount ?? 0u;
        uint lightRevision =
            _sceneGpuData?.CurrentLightRevision ?? 0u;
        uint skyRevision =
            _sceneGpuData?.CurrentSkyRevision ?? 0u;
        _cameraProvider.TryGetViewportCameraData(
            context.Width,
            context.Height,
            out Vector3 cameraPosition,
            out _,
            out _);

        UpdatePushData push = new()
        {
            FrameNumber = (uint)context.FrameNumber,
            LightCount = lightCount,
            RaysPerProbe = RaysPerProbe,
            MaxUpdates = (uint)admittedCount,
            CameraPositionAndJitter = new Vector4(
                cameraPosition,
                0f),
            AtlasUVParams = new Vector4(
                _atlas.IrradianceBindlessIndex,
                _atlas.VisibilityBindlessIndex,
                DDGIAtlasResources.AtlasWidth,
                0f),
            OriginAndProbeCountZ = new Vector4(_atlas.Origin, 0f),
            Extent = new Vector4(
                MaxTraceDistanceMeters,
                0f,
                0f,
                0f),
            SkySunDirectionAndRadius =
                _sceneGpuData?.CurrentSkySunDirectionAndRadius ??
                new Vector4(0f, 1f, 0f, 0.00465f),
            SkyAtmosphereParameters =
                _sceneGpuData?.CurrentSkyAtmosphereParameters ??
                new Vector4(1f, 2f, 0.1f, 0f),
            MaxProbeBudget = (uint)_atlas.MaxProbesTotalBudget,
            UseSceneTlas = useSceneTlas ? 1u : 0u,
            RadianceRevision = DDGIAtlasResources.PackRadianceRevision(
                lightRevision,
                skyRevision),
            MinimumProbeClearance =
                DDGIProbePlacementPass.ProbeFreeSpaceRadiusMeters * 0.75f,
            ProbePositions = _atlas.ProbePositions.DeviceAddress,
            Lights = lightBuffer?.DeviceAddress ?? 0ul,
            ProbeCounter = _atlas.ProbeCounter.DeviceAddress,
            ProbeStates = _atlas.ProbeStates.DeviceAddress,
            ProbeUpdateQueue = _atlas.ProbeUpdateQueue.DeviceAddress,
            Instances = instanceBuffer?.DeviceAddress ?? 0ul,
            Parts = partBuffer?.DeviceAddress ?? 0ul,
            Materials = materialBuffer?.DeviceAddress ?? 0ul,
            InstanceCount = _sceneGpuData?.CurrentInstanceCount ?? 0u,
            PartCount = _sceneGpuData?.CurrentPartCount ?? 0u,
            MaterialCount = _sceneGpuData?.CurrentMaterialCount ?? 0u,
        };

        sink.BeginComputePass(Name);
        sink.BindPipeline(_pipeline);
        sink.UseBuffer(_atlas.ProbePositions, 3);
        if (lightBuffer != null)
            sink.UseBuffer(lightBuffer, 1);
        sink.UseBuffer(_atlas.ProbeCounter, 1);
        sink.UseBuffer(_atlas.ProbeStates, 3);
        sink.UseBuffer(_atlas.ProbeUpdateQueue, 1);
        if (instanceBuffer != null)
            sink.UseBuffer(instanceBuffer, 1);
        if (partBuffer != null)
            sink.UseBuffer(partBuffer, 1);
        if (materialBuffer != null)
            sink.UseBuffer(materialBuffer, 1);
        if (_atlas.SharedHeap.IsInitialized)
        {
            sink.BindHeap(1, _atlas.SharedHeap);
            sink.BindSampler(0, _sampler);
        }
        sink.BindTexture(0, _atlas.Irradiance);
        sink.BindTexture(4, _atlas.Visibility);
        if (useSceneTlas)
        {
            sink.BindAccelStruct(3, tlasInfo.SceneTlas!);
            sink.UseAccelStruct(tlasInfo.SceneTlas!, 1);
        }
        sink.PushConstants(
            0,
            (uint)sizeof(UpdatePushData),
            (IntPtr)(&push));
        sink.Dispatch(
            (uint)admittedCount,
            1,
            1,
            RaysPerProbe,
            1,
            1);
        sink.EndComputePass();
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _computeShader.Dispose();
        _sampler.Dispose();
    }

    /// <inheritdoc />
    public bool TryGetSubmittedUnitCount(
        long frameNumber,
        out int unitCount)
    {
        int submissionSlot = (int)(
            frameNumber % SubmissionHistoryCapacity);
        if (submissionSlot >= 0 &&
            _submissionFrames[submissionSlot] == frameNumber &&
            _submissionTimingEligible[submissionSlot])
        {
            unitCount = _submissionCounts[submissionSlot];
            return true;
        }

        unitCount = 0;
        return false;
    }
}
