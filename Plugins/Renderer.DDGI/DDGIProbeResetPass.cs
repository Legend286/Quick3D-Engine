// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.RenderGraph.Shaders;

namespace Engine.DDGI;

/// <summary>Resets GPU-owned DDGI frame counters and indirect arguments.</summary>
public sealed class DDGIProbeResetPass : RenderPass, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ResetPushData
    {
        public ulong ProbeCounter;
        public ulong ProbeDrawArgs;
        public ulong VolumeState;
        public uint ProbeCapacity;
        public uint Padding0;
    }

    private readonly DDGIAtlasResources _atlas;
    private readonly RhiShader _shader;
    private readonly RhiPipeline _pipeline;

    public DDGIProbeResetPass(
        RhiDevice device,
        string shaderSource,
        DDGIAtlasResources atlas,
        IReadOnlyList<string>? includeDirs,
        IReadOnlyList<string>? cliArgs,
        ShaderCompileCache? compileCache)
    {
        _atlas = atlas;
        Name = "DDGI Probe Reset";
        Queue = RhiNative.QueueType.Graphics;
        _shader = compileCache == null
            ? RhiShader.FromSource(
                device,
                shaderSource,
                "resetMain",
                RhiNative.ShaderStage.Compute,
                includeDirs,
                cliArgs)
            : (RhiShader)compileCache.GetOrCompileHash(
                shaderSource,
                "resetMain",
                RhiNative.ShaderStage.Compute,
                includeDirs,
                cliArgs,
                () => RhiShader.FromSource(
                    device,
                    shaderSource,
                    "resetMain",
                    RhiNative.ShaderStage.Compute,
                    includeDirs,
                    cliArgs));
        _pipeline = RhiPipeline.CreateCompute(device, _shader);
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeCounter);
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeDrawArgs);
        builder.ImportBuffer(_atlas.ResourceHandles.VolumeState);
        builder.Write(
            _atlas.ResourceHandles.ProbeCounter,
            ResourceState.UnorderedAccess);
        builder.Write(
            _atlas.ResourceHandles.ProbeDrawArgs,
            ResourceState.UnorderedAccess);
        builder.Read(
            _atlas.ResourceHandles.VolumeState,
            ResourceState.ShaderRead);
    }

    public override unsafe void Execute(
        ICommandSink sink,
        RenderGraphContext context)
    {
        ResetPushData push = new()
        {
            ProbeCounter = _atlas.ProbeCounter.DeviceAddress,
            ProbeDrawArgs = _atlas.ProbeDrawArgs.DeviceAddress,
            VolumeState = _atlas.VolumeState.DeviceAddress,
            ProbeCapacity = (uint)_atlas.CoarseGridCells
        };
        sink.BeginComputePass(Name);
        sink.BindPipeline(_pipeline);
        sink.UseBuffer(_atlas.ProbeCounter, 2);
        sink.UseBuffer(_atlas.ProbeDrawArgs, 2);
        sink.UseBuffer(_atlas.VolumeState, 1);
        sink.PushConstants(
            0,
            (uint)sizeof(ResetPushData),
            (IntPtr)(&push));
        sink.Dispatch(1, 1, 1, 1, 1, 1);
        sink.EndComputePass();
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _shader.Dispose();
    }
}
