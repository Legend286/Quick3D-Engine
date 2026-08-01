// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.RenderGraph.Shaders;

namespace Engine.Renderer;

/// <summary>Shades visibility-buffer surfaces in compute tiles.</summary>
internal sealed class VisibilityShadingPass : RenderPass, IDisposable
{
    private const uint ThreadGroupSize = 8;
    private const uint RenderSkyFlag = 0x80000000u;
    private readonly PbrPass _owner;
    private readonly RhiShader _shader;
    private readonly RhiPipeline _pipeline;

    public VisibilityShadingPass(
        RhiDevice device,
        string contentRoot,
        PbrPass owner,
        IReadOnlyList<string>? cliArgs,
        IReadOnlyList<string>? includeDirs,
        ShaderCompileCache? compileCache)
    {
        _owner = owner;
        Name = "Visibility PBR Shading";
        Queue = RhiNative.QueueType.Graphics;
        IReadOnlyList<string> resolvedIncludeDirs =
            includeDirs ??
            new[] { Path.Combine(contentRoot, "shaders") };
        string source = VisibilityBufferPass.LoadShaderSource(
            contentRoot,
            "visibility_shade.slang",
            resolvedIncludeDirs);
        _shader = Compile(
            device,
            source,
            cliArgs,
            resolvedIncludeDirs,
            compileCache);
        _pipeline = RhiPipeline.CreateCompute(device, _shader);
        _pipeline.SetDebugName(
            "Visibility 8x8 Tile PBR",
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
        _owner.SetupShadingReads(builder);
    }

    public override unsafe void Execute(
        ICommandSink sink,
        RenderGraphContext context)
    {
        _owner.EnsurePrepared(sink, context);
        ScenePushData push = _owner.PreparedPush;
        ViewportDebugView debugView =
            (ViewportDebugView)(push.DebugFlags & 0xffu);
        if (!IsShadingView(debugView))
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

        if (_owner.RenderSkyEnabled)
            push.DebugFlags |= RenderSkyFlag;
        uint width = Math.Max(context.Width, 1u);
        uint height = Math.Max(context.Height, 1u);
        sink.BeginComputePass(Name);
        sink.BindPipeline(_pipeline);
        _owner.BindShadingResources(sink);
        sink.BindTexture(4, identifiers);
        sink.BindTexture(5, barycentrics);
        sink.BindTexture(6, depth);
        sink.BindTexture(7, output);
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
        _pipeline.Dispose();
        _shader.Dispose();
    }

    internal static bool IsShadingView(ViewportDebugView view)
        => view == ViewportDebugView.VisibilityPbr;

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
