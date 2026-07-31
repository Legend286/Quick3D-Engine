// SPDX-License-Identifier: MIT
using System;
using Engine.RenderGraph;
using Engine.RHI;
using Engine.CBindings;

namespace Engine.Renderer;

public sealed class ImGuiPass : RenderPass
{
    private readonly ImGuiRenderer _renderer;

    public ImGuiPass(ImGuiRenderer renderer)
    {
        _renderer = renderer;
        Name = "ImGuiPass";
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Write(RenderGraphResources.BackBufferHandle, ResourceState.RenderTarget);
    }

    public override void Execute(ICommandSink sink, RenderGraphContext context)
    {
        if (!context.TryGetTexture(RenderGraphResources.BackBufferHandle, out RhiTexture colorTarget))
            return;

        uint w = context.Width > 0 ? context.Width : 1280;
        uint h = context.Height > 0 ? context.Height : 720;

        // Use LoadOp.Load so we draw over the existing scene
        sink.BeginRenderPass(colorTarget,
                             RhiNative.LoadOp.Load,
                             RhiNative.StoreOp.Store,
                             depth: null);

        sink.SetViewport(0, 0, w, h);

        _renderer.Render(sink);

        sink.EndPass();
    }
}
