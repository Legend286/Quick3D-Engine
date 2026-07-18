using System;
using System.IO;
using System.Runtime.InteropServices;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;

namespace Engine.Game;

public sealed class OutlineCompositePass : RenderPass, IDisposable
{
    private readonly RhiDevice _device;
    private readonly string _contentRoot;
    private readonly Renderer _renderer;

    private RhiShader _vs;
    private RhiShader _fs;
    private RhiPipeline? _pipeline;
    private RhiSampler _sampler;
    private RhiBindlessHeap _bindlessHeap;

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositePushData
    {
        public uint MaskTextureSlot;
        public uint Width;
        public uint Height;
        public uint Pad;
    }

    public OutlineCompositePass(RhiDevice device, string contentRoot, Renderer renderer, RhiBindlessHeap sharedBindlessHeap)
    {
        _device = device;
        _contentRoot = contentRoot;
        _renderer = renderer;
        _bindlessHeap = sharedBindlessHeap;
        Name = "OutlineCompositePass";

        string shaderDir = Path.Combine(_contentRoot, "shaders");
        string src = File.ReadAllText(Path.Combine(shaderDir, "outline_composite.slang"));
        _vs = RhiShader.FromSource(_device, src, "vertexMain", RhiNative.ShaderStage.Vertex, shaderDir);
        _fs = RhiShader.FromSource(_device, src, "fragmentMain", RhiNative.ShaderStage.Fragment, shaderDir);

        _sampler = RhiSampler.Create(_device);

        // Alpha blend so we can just draw over the backbuffer where there is an outline
        _pipeline = RhiPipeline.CreateGraphics(
            _device, _vs, _fs,
            RhiNative.TextureFormat.Bgra8Unorm,
            enableDepth: false,
            enableBlend: true); 
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Read(Engine.Game.Renderer.OutlineMaskHandle, ResourceState.ShaderRead);
        builder.Write(Engine.Game.Renderer.BackBufferHandle, ResourceState.RenderTarget);
    }

    public override unsafe void Execute(ICommandSink sink, RenderGraphContext context)
    {
        ulong selectedId = _renderer.SelectedEntity;
        if (selectedId == 0) return; // Nothing selected

        if (!context.TryGetTexture(Engine.Game.Renderer.BackBufferHandle, out RhiTexture backBuffer)) return;
        if (!context.TryGetTexture(Engine.Game.Renderer.OutlineMaskHandle, out RhiTexture maskTexture)) return;

        uint w = context.Width > 0 ? context.Width : 1280;
        uint h = context.Height > 0 ? context.Height : 720;

        uint maskSlot = _bindlessHeap.Register(maskTexture);

        sink.BeginRenderPass(backBuffer, Engine.CBindings.RhiNative.LoadOp.Load, Engine.CBindings.RhiNative.StoreOp.Store);
        if (_pipeline != null) sink.BindPipeline(_pipeline);

        var push = new CompositePushData
        {
            MaskTextureSlot = maskSlot,
            Width = w,
            Height = h,
            Pad = 0
        };

        sink.PushConstants(0, (uint)sizeof(CompositePushData), new IntPtr(&push));
        sink.BindSampler(0, _sampler);
        sink.BindHeap(1, _bindlessHeap);
        sink.Draw(3, 1, 0, 0); // Fullscreen triangle

        sink.EndPass();
    }

    public void Dispose()
    {
        _pipeline?.Dispose();
        _vs.Dispose();
        _fs.Dispose();
        _sampler?.Dispose();
    }
}
