// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.RenderGraph.Shaders;
using Engine.Scene;

namespace Engine.DDGI;

/// <summary>
/// Phase-3 HW-RT compute pass that updates the top-N DDGI probes.
/// Each frame runs at most <see cref="MaxProbesPerFrame"/> probes
/// × <see cref="RaysPerProbe"/> rays on the async-compute queue
/// with <c>[numthreads(32,1,1)]</c> matching the kernel's
/// numthreads declaration. The BLAS cache + TLAS rebuild is
/// delegated to <see cref="RaytracingSceneCache"/> — same path
/// as the canonical path-tracer uses, so DDGI plugins don't
/// rebuild the same mesh-BLAS set twice on systems that have
/// BOTH plugins loaded.
/// </summary>
public sealed class DDGIProbeUpdatePass : RenderPass
{
    public const int MaxProbesPerFrame = 8;
    public const int RaysPerProbe = 32;

    private readonly RhiDevice _device;
    private readonly RhiShader _computeShader;
    private readonly RhiPipeline _pipeline;
    private readonly DDGIAtlasResources _atlas;
    private readonly RaytracingSceneCache _sceneCache;
    private readonly DDGIRendererPlugin _plugin;
    private readonly Engine.RenderGraph.GpuWorkScheduler _scheduler;
    private IReadOnlyList<int> _probeIndices = Array.Empty<int>();
    private Vector3 _cameraPosition;
    private long _frameNumber;
    private RhiAccelStruct? _sceneTlas;

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
    }

    public DDGIProbeUpdatePass(
        RhiDevice device,
        IEntityStore world,
        string shaderSource,
        IReadOnlyList<string>? includeDirs,
        IReadOnlyList<string>? cliArgs,
        DDGIAtlasResources atlas,
        DDGIRendererPlugin plugin,
        Engine.RenderGraph.GpuWorkScheduler scheduler,
        ShaderCompileCache? compileCache = null)
    {
        if (atlas == null) throw new ArgumentNullException(nameof(atlas));
        if (plugin == null) throw new ArgumentNullException(nameof(plugin));
        if (scheduler == null) throw new ArgumentNullException(nameof(scheduler));
        if (world == null) throw new ArgumentNullException(nameof(world));

        _device = device;
        _atlas = atlas;
        _sceneCache = new RaytracingSceneCache(device, world);
        _plugin = plugin;
        _scheduler = scheduler;

        Name = "DDGI Probe Update";
        Queue = RhiNative.QueueType.Compute;

        string shaderName = "shaders/ddgi_probe_update.slang";

        if (compileCache == null)
        {
            _computeShader = RhiShader.FromSource(
                device, shaderSource, "computeMain",
                RhiNative.ShaderStage.Compute,
                includeDirs, cliArgs);
        }
        else
        {
            _computeShader = (RhiShader)compileCache.GetOrCompileHash(
                shaderSource, "computeMain",
                RhiNative.ShaderStage.Compute,
                includeDirs, cliArgs,
                () => RhiShader.FromSource(
                    device, shaderSource, "computeMain",
                    RhiNative.ShaderStage.Compute,
                    includeDirs, cliArgs));
        }
        _computeShader.SetDebugName("DDGI Probe Update CS", "DDGI");

        _pipeline = RhiPipeline.CreateCompute(_device, _computeShader);
        _pipeline.SetDebugName("DDGI Probe Update Pipeline", "DDGI");
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Write(RenderGraphResources.BackBufferHandle, ResourceState.UnorderedAccess);
    }

    public override unsafe void Execute(ICommandSink sink, RenderGraphContext context)
    {
        _probeIndices = _plugin.EvaluateFrameUpdates(_scheduler, out _cameraPosition, out _frameNumber);
        
        if (_atlas == null || _probeIndices.Count == 0) return;

        RaytracingSceneCache.TlasUpdateResult tlasInfo =
            _sceneCache.TryUpdateTlas(sink);
        _sceneTlas = tlasInfo.SceneTlas;
        if (_sceneTlas == null) return;

        float jitter = Fract(
            (float)_frameNumber * 0.61803398875f);

        int band = (int)(jitter * 4.0f);
        band = band < 0 ? 0 : (band > 3 ? 3 : band);

        Span<uint> probeIndexSlots = stackalloc uint[MaxProbesPerFrame];
        uint admittedCount = (uint)_probeIndices.Count;
        if (admittedCount > MaxProbesPerFrame)
        {
            throw new InvalidOperationException(
                $"DDGI probe-update pass admitted {admittedCount} probes " +
                $"but the shader push struct only carries " +
                $"{MaxProbesPerFrame} inline ProbeIndex slots. Raise " +
                $"MaxProbesPerFrame + the push struct fields in lockstep " +
                $"and extend the shader's switch to cover the new range.");
        }
        for (int i = 0; i < MaxProbesPerFrame; ++i)
        {
            int idx = i < (int)admittedCount ? _probeIndices[i] : -1;
            probeIndexSlots[i] = idx < 0 ? uint.MaxValue : (uint)idx;
        }

        UpdatePushData push = new()
        {
            ProbeCount = admittedCount,
            FrameNumber = (uint)_frameNumber,
            LightCount = (uint)_atlas.LightSlotCount,
            RaysPerProbe = (uint)RaysPerProbe,
            CameraPositionAndJitter = new Vector4(
                _cameraPosition.X,
                _cameraPosition.Y,
                _cameraPosition.Z,
                jitter),
            AtlasUVParams = new Vector4(
                _atlas.IrradianceBindlessIndex,
                _atlas.VisibilityBindlessIndex,
                0f, 0f),
            OriginAndProbeCountZ = new Vector4(
                _atlas.Origin.X,
                _atlas.Origin.Y,
                _atlas.Origin.Z,
                admittedCount),
            Extent = new Vector4(
                _atlas.Extent.X,
                _atlas.Extent.Y,
                _atlas.Extent.Z,
                0f),
            ProbeIndex0 = probeIndexSlots[0],
            ProbeIndex1 = probeIndexSlots[1],
            ProbeIndex2 = probeIndexSlots[2],
            ProbeIndex3 = probeIndexSlots[3],
            ProbeIndex4 = probeIndexSlots[4],
            ProbeIndex5 = probeIndexSlots[5],
            ProbeIndex6 = probeIndexSlots[6],
            ProbeIndex7 = probeIndexSlots[7],
            TreeRootIndex = (uint)Math.Max(0, _atlas.TreeRootIndex),
            TreeNodeCount = (uint)_atlas.TreeNodeCount,
            TreeLeafVisitBudget = DDGIRendererPlugin.LeafVisitBudget,
        };

        sink.BeginComputePass(Name);
        sink.BindPipeline(_pipeline);
        sink.UseBuffer(_atlas.ProbePositions, 5);
        sink.UseBuffer(_atlas.Lights, 1);
        sink.UseBuffer(_atlas.LightTreeNodes, 2);
        sink.BindTexture(0, _atlas.Irradiance);
        sink.BindTexture(4, _atlas.Visibility);

        if (_atlas.SharedHeap != null && _atlas.SharedHeap.IsInitialized)
        {
            sink.BindHeap(1, _atlas.SharedHeap);
        }

        sink.BindAccelStruct(3, _sceneTlas);
        sink.UseAccelStruct(_sceneTlas, 1);

        sink.PushConstants(0, (uint)sizeof(UpdatePushData),
            (IntPtr)(&push));

        sink.Dispatch(admittedCount, 1, 1, 1, 1, 1);
        sink.EndComputePass();
    }

    public void Dispose()
    {
        _pipeline?.Dispose();
        _computeShader?.Dispose();
        _sceneCache?.Dispose();
    }

    private static float Fract(float v)
    {
        float floor = (float)MathF.Floor(v);
        return v - floor;
    }
}
