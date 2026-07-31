// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.RenderGraph.Shaders;
using Engine.Scene;

namespace Engine.DDGI;

/// <summary>
/// Updates the dense GPU-owned DDGI probe prefix. Placement writes the
/// accepted probe count to the counter buffer; this pass dispatches the full
/// budget and each thread group culls itself against that counter, so no probe
/// list or GPU readback is owned by the CPU.
/// </summary>
public sealed class DDGIProbeUpdatePass : RenderPass, IDisposable
{
    public const int MaxProbesPerFrame = 8;
    public const int RaysPerProbe = 32;

    private readonly RhiShader _computeShader;
    private readonly RhiPipeline _pipeline;
    private readonly DDGIAtlasResources _atlas;
    private readonly RaytracingSceneCache _sceneCache;

    [StructLayout(LayoutKind.Sequential)]
    private struct UpdatePushData
    {
        public uint ProbeCount;
        public uint FrameNumber;
        public uint LightCount;
        public uint RaysPerProbe;
        public Vector4 CameraPositionAndJitter;
        public Vector4 AtlasUVParams;
        public Vector4 OriginAndProbeCountZ;
        public Vector4 Extent;
        public uint ProbeIndex0;
        public uint ProbeIndex1;
        public uint ProbeIndex2;
        public uint ProbeIndex3;
        public uint ProbeIndex4;
        public uint ProbeIndex5;
        public uint ProbeIndex6;
        public uint ProbeIndex7;
        public uint TreeRootIndex;
        public uint TreeNodeCount;
        public uint TreeLeafVisitBudget;
        public uint PaddingTail3;
        public uint MaxProbeBudget;
        public uint UseSceneTlas;
        public uint Padding0;
        public uint Padding1;
        public uint Padding2;
        public ulong ProbePositions;
        public ulong Lights;
        public ulong LightTree;
        public ulong ProbeCounter;
    }

    public DDGIProbeUpdatePass(
        RhiDevice device,
        IEntityStore world,
        string shaderSource,
        IReadOnlyList<string>? includeDirs,
        IReadOnlyList<string>? cliArgs,
        DDGIAtlasResources atlas,
        ShaderCompileCache? compileCache = null)
    {
        if (world == null) throw new ArgumentNullException(nameof(world));
        if (atlas == null) throw new ArgumentNullException(nameof(atlas));

        _atlas = atlas;
        _sceneCache = new RaytracingSceneCache(device, world);
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
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.ImportBuffer(_atlas.ResourceHandles.ProbePositions);
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeCounter);
        builder.ImportBuffer(_atlas.ResourceHandles.Lights);
        builder.ImportBuffer(_atlas.ResourceHandles.LightTreeNodes);
        builder.ImportTexture(_atlas.ResourceHandles.Irradiance);
        builder.ImportTexture(_atlas.ResourceHandles.Visibility);
        builder.Read(
            _atlas.ResourceHandles.ProbePositions,
            ResourceState.ShaderRead);
        builder.Read(
            _atlas.ResourceHandles.ProbeCounter,
            ResourceState.ShaderRead);
        builder.Read(
            _atlas.ResourceHandles.Lights,
            ResourceState.ShaderRead);
        builder.Read(
            _atlas.ResourceHandles.LightTreeNodes,
            ResourceState.ShaderRead);
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
        RaytracingSceneCache.TlasUpdateResult tlasInfo;
        try
        {
            tlasInfo = _sceneCache.TryUpdateTlas(sink);
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

        float jitter = Fract((float)context.FrameNumber * 0.61803398875f);
        UpdatePushData push = new()
        {
            ProbeCount = 0u,
            FrameNumber = (uint)context.FrameNumber,
            LightCount = (uint)_atlas.UploadedLightCount,
            RaysPerProbe = RaysPerProbe,
            CameraPositionAndJitter = new Vector4(0f, 0f, 0f, jitter),
            AtlasUVParams = new Vector4(
                _atlas.IrradianceBindlessIndex,
                _atlas.VisibilityBindlessIndex,
                0f,
                0f),
            OriginAndProbeCountZ = new Vector4(_atlas.Origin, 0f),
            Extent = new Vector4(_atlas.Extent, 0f),
            ProbeIndex0 = uint.MaxValue,
            ProbeIndex1 = uint.MaxValue,
            ProbeIndex2 = uint.MaxValue,
            ProbeIndex3 = uint.MaxValue,
            ProbeIndex4 = uint.MaxValue,
            ProbeIndex5 = uint.MaxValue,
            ProbeIndex6 = uint.MaxValue,
            ProbeIndex7 = uint.MaxValue,
            TreeRootIndex = (uint)Math.Max(0, _atlas.TreeRootIndex),
            TreeNodeCount = (uint)_atlas.TreeNodeCount,
            TreeLeafVisitBudget = DDGIRendererPlugin.LeafVisitBudget,
            MaxProbeBudget = (uint)_atlas.MaxProbesTotalBudget,
            UseSceneTlas = useSceneTlas ? 1u : 0u,
            ProbePositions = _atlas.ProbePositions.DeviceAddress,
            Lights = _atlas.Lights.DeviceAddress,
            LightTree = _atlas.LightTreeNodes.DeviceAddress,
            ProbeCounter = _atlas.ProbeCounter.DeviceAddress,
        };

        sink.BeginComputePass(Name);
        sink.BindPipeline(_pipeline);
        sink.UseBuffer(_atlas.ProbePositions, 5);
        sink.UseBuffer(_atlas.Lights, 1);
        sink.UseBuffer(_atlas.LightTreeNodes, 2);
        sink.UseBuffer(_atlas.ProbeCounter, 1);
        sink.BindTexture(0, _atlas.Irradiance);
        sink.BindTexture(4, _atlas.Visibility);
        if (_atlas.SharedHeap != null && _atlas.SharedHeap.IsInitialized)
            sink.BindHeap(1, _atlas.SharedHeap);
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
            (uint)_atlas.MaxProbesTotalBudget,
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
        _sceneCache.Dispose();
    }

    private static float Fract(float value)
        => value - MathF.Floor(value);
}
