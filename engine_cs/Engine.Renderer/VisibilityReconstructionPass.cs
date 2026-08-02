// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.RenderGraph.Shaders;

namespace Engine.Renderer;

/// <summary>Reconstructs surface attributes from visibility-buffer data.</summary>
internal sealed class VisibilityReconstructionPass : RenderPass, IDisposable
{
    private const uint ThreadGroupSize = 8;
    private readonly RasterSceneGpuCache _sceneCache;
    private readonly PbrPass _owner;
    private readonly RhiShader _shader;
    private readonly RhiPipeline _pipeline;
    private readonly RhiSampler _sampler;

    public VisibilityReconstructionPass(
        RhiDevice device,
        string contentRoot,
        RasterSceneGpuCache sceneCache,
        PbrPass owner,
        IReadOnlyList<string>? cliArgs,
        IReadOnlyList<string>? includeDirs,
        ShaderCompileCache? compileCache)
    {
        _sceneCache = sceneCache;
        _owner = owner;
        Name = "Visibility Reconstruction";
        Queue = RhiNative.QueueType.Graphics;
        IReadOnlyList<string> resolvedIncludeDirs =
            includeDirs ??
            new[] { Path.Combine(contentRoot, "shaders") };
        string source = VisibilityBufferPass.LoadShaderSource(
            contentRoot,
            "visibility_reconstruct.slang",
            resolvedIncludeDirs);
        _shader = Compile(
            device,
            source,
            cliArgs,
            resolvedIncludeDirs,
            compileCache);
        _pipeline = RhiPipeline.CreateCompute(device, _shader);
        _sampler = RhiSampler.Create(device);
        _pipeline.SetDebugName(
            "Visibility Attribute Reconstruction",
            "Visibility Buffer");
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Read(
            RenderGraphResources.VisibilityIdentifiersHandle,
            ResourceState.ShaderRead);
        builder.Read(
            RenderGraphResources.VisibilityBarycentricsHandle,
            ResourceState.ShaderRead);
        builder.Read(
            RenderGraphResources.DepthBufferHandle,
            ResourceState.ShaderRead);
        builder.Write(
            RenderGraphResources.VisibilityReconstructionHandle,
            ResourceState.UnorderedAccess);
    }

    public override unsafe void Execute(
        ICommandSink sink,
        RenderGraphContext context)
    {
        _owner.EnsurePrepared(sink, context);
        ScenePushData push = _owner.PreparedPush;
        ViewportDebugView debugView =
            (ViewportDebugView)(push.DebugFlags & 0xffu);
        if (!IsReconstructionView(debugView))
            return;
        if (!context.TryGetTexture(
                RenderGraphResources.VisibilityIdentifiersHandle,
                out RhiTexture identifiers) ||
            !context.TryGetTexture(
                RenderGraphResources.VisibilityBarycentricsHandle,
                out RhiTexture barycentrics) ||
            !context.TryGetTexture(
                RenderGraphResources.DepthBufferHandle,
                out RhiTexture depth) ||
            !context.TryGetTexture(
                RenderGraphResources.VisibilityReconstructionHandle,
                out RhiTexture output))
        {
            return;
        }

        SceneFrameData frameData = _owner.PreparedFrameData;
        uint width = Math.Max(context.Width, 1u);
        uint height = Math.Max(context.Height, 1u);
        sink.BeginComputePass(Name);
        sink.BindPipeline(_pipeline);
        sink.UseBuffer(_sceneCache.InstanceBuffer, 1);
        sink.UseBuffer(_sceneCache.PartBuffer, 1);
        sink.UseBuffer(_sceneCache.MaterialBuffer, 1);
        frameData.BindGeometry(sink);
        sink.BindTexture(4, identifiers);
        sink.BindTexture(5, barycentrics);
        sink.BindTexture(6, depth);
        sink.BindTexture(0, output);
        if (_owner.BindlessHeap.IsInitialized)
        {
            sink.BindHeap(1, _owner.BindlessHeap);
            sink.BindSampler(0, _sampler);
        }
        sink.PushConstants(
            0,
            (uint)sizeof(ScenePushData),
            (IntPtr)(&push));
        sink.Dispatch(
            (width + ThreadGroupSize - 1u) / ThreadGroupSize,
            (height + ThreadGroupSize - 1u) / ThreadGroupSize,
            1,
            ThreadGroupSize,
            ThreadGroupSize,
            1);
        sink.EndComputePass();
    }

    public void Dispose()
    {
        _sampler.Dispose();
        _pipeline.Dispose();
        _shader.Dispose();
    }

    internal static bool IsReconstructionView(ViewportDebugView view)
        => view is
            ViewportDebugView.ReconstructedPosition or
            ViewportDebugView.ReconstructedNormal or
            ViewportDebugView.ReconstructedUv or
            ViewportDebugView.ReconstructedMaterial or
            ViewportDebugView.ReconstructedInstance or
            ViewportDebugView.ReconstructedTangent;

    private static RhiShader Compile(
        RhiDevice device,
        string source,
        IReadOnlyList<string>? cliArgs,
        IReadOnlyList<string>? includeDirs,
        ShaderCompileCache? compileCache)
    {
        if (compileCache == null)
        {
            return RhiShader.FromSource(
                device,
                source,
                "computeMain",
                RhiNative.ShaderStage.Compute,
                includeDirs,
                cliArgs);
        }
        return (RhiShader)compileCache.GetOrCompileHash(
            source,
            "computeMain",
            RhiNative.ShaderStage.Compute,
            includeDirs,
            cliArgs,
            () => RhiShader.FromSource(
                device,
                source,
                "computeMain",
                RhiNative.ShaderStage.Compute,
                includeDirs,
                cliArgs));
    }
}
