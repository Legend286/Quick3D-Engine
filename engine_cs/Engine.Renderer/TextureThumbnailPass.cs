// SPDX-License-Identifier: MIT
using System.IO;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;

namespace Engine.Renderer;

public sealed class TextureThumbnailPass : RenderPass, System.IDisposable
{
    private readonly RhiDevice _device;
    private readonly RhiTexture _sourceTexture;
    private readonly string _contentRoot;
    private readonly Renderer _renderer;
    private readonly RhiShader _vs;
    private readonly RhiShader _fs;
    private readonly RhiPipeline _pipeline;
    private readonly RhiSampler _sampler;

    public TextureThumbnailPass(RhiDevice device, RhiTexture sourceTexture, string contentRoot, Renderer renderer)
    {
        _device = device;
        _sourceTexture = sourceTexture;
        _contentRoot = contentRoot;
        _renderer = renderer;
        Name = "TextureThumbnailPass";

        string blitSrc = _renderer.LoadShaderSource("shaders/blit.slang", _contentRoot);
        _vs = RhiShader.FromSource(_device, blitSrc, "vertexMain", RhiNative.ShaderStage.Vertex, _renderer.ActiveShaderIncludeDirs, _renderer.ActiveShaderCliArgs);
        _fs = RhiShader.FromSource(_device, blitSrc, "fragmentMain", RhiNative.ShaderStage.Fragment, _renderer.ActiveShaderIncludeDirs, _renderer.ActiveShaderCliArgs);
        _pipeline = RhiPipeline.CreateGraphics(_device, _vs, _fs, RhiNative.TextureFormat.Bgra8Unorm, enableDepth: false);
        _sampler = RhiSampler.Create(_device);
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Write(RenderGraphResources.BackBufferHandle, ResourceState.RenderTarget);
    }

    public override void Execute(ICommandSink sink, RenderGraphContext context)
    {
        if (!context.TryGetTexture(RenderGraphResources.BackBufferHandle, out var backBuffer))
            return;

        uint w = context.Width > 0 ? context.Width : 256;
        uint h = context.Height > 0 ? context.Height : 256;

        sink.BeginRenderPass(backBuffer, RhiNative.LoadOp.Clear, RhiNative.StoreOp.Store);
        sink.BindPipeline(_pipeline);
        sink.SetViewport(0, 0, w, h);
        sink.SetScissor(0, 0, w, h);
        sink.BindTexture(0, _sourceTexture);
        sink.BindSampler(0, _sampler);
        sink.Draw(3);
        sink.EndPass();
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _vs.Dispose();
        _fs.Dispose();
        _sampler.Dispose();
    }
}
