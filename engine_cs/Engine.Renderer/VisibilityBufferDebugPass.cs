// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Runtime.InteropServices;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;

namespace Engine.Renderer;

/// <summary>Presents visibility shading and diagnostic comparisons.</summary>
internal sealed class VisibilityBufferDebugPass : RenderPass, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct VisibilityDebugPush
    {
        public uint Width;
        public uint Height;
        public uint Mode;
        public uint Padding;
    }

    private readonly Renderer _renderer;
    private readonly RhiShader _vertexShader;
    private readonly RhiShader _fragmentShader;
    private readonly RhiPipeline _pipeline;

    public VisibilityBufferDebugPass(
        RhiDevice device,
        string contentRoot,
        Renderer renderer)
    {
        _renderer = renderer;
        Name = "Visibility Present";
        string source = renderer.LoadShaderSource(
            "shaders/visibility_buffer_debug.slang",
            contentRoot);
        var includeDirs = renderer.ActiveShaderIncludeDirs ??
            new[] { Path.Combine(contentRoot, "shaders") };
        _vertexShader = RhiShader.FromSource(
            device,
            source,
            "vertexMain",
            RhiNative.ShaderStage.Vertex,
            includeDirs,
            renderer.ActiveShaderCliArgs);
        _fragmentShader = RhiShader.FromSource(
            device,
            source,
            "fragmentMain",
            RhiNative.ShaderStage.Fragment,
            includeDirs,
            renderer.ActiveShaderCliArgs);
        _pipeline = RhiPipeline.CreateGraphics(
            device,
            _vertexShader,
            _fragmentShader,
            RhiNative.TextureFormat.Bgra8Unorm,
            enableDepth: false);
        _pipeline.SetDebugName(
            "Visibility Buffer Present Composite",
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
        builder.Read(
            RenderGraphResources.VisibilityReconstructionHandle,
            ResourceState.ShaderRead);
        builder.Write(
            RenderGraphResources.BackBufferHandle,
            ResourceState.RenderTarget);
    }

    public override unsafe void Execute(
        ICommandSink sink,
        RenderGraphContext context)
    {
        bool visibilityView =
            _renderer.DebugView == ViewportDebugView.VisibilityBuffer;
        bool reconstructionView =
            VisibilityReconstructionPass.IsReconstructionView(
                _renderer.DebugView);
        bool shadingView = VisibilityShadingPass.IsShadingView(
            _renderer.DebugView);
        if (!visibilityView && !reconstructionView && !shadingView)
            return;
        if (!context.TryGetTexture(
                RenderGraphResources.BackBufferHandle,
                out RhiTexture backBuffer) ||
            !context.TryGetTexture(
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
                out RhiTexture reconstruction))
        {
            return;
        }

        uint width = Math.Max(context.Width, 1u);
        uint height = Math.Max(context.Height, 1u);
        VisibilityDebugPush push = new()
        {
            Width = width,
            Height = height,
            Mode = (uint)_renderer.DebugView,
        };
        sink.BeginRenderPass(
            backBuffer,
            RhiNative.LoadOp.Clear,
            RhiNative.StoreOp.Store);
        sink.BindPipeline(_pipeline);
        sink.SetViewport(0, 0, width, height);
        sink.SetScissor(0, 0, width, height);
        sink.PushConstants(
            0,
            (uint)sizeof(VisibilityDebugPush),
            (IntPtr)(&push));
        sink.BindTexture(0, identifiers);
        sink.BindTexture(1, barycentrics);
        sink.BindTexture(2, depth);
        sink.BindTexture(3, reconstruction);
        sink.BindTexture(4, reconstruction);
        sink.Draw(3);
        sink.EndPass();
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _fragmentShader.Dispose();
        _vertexShader.Dispose();
    }
}
