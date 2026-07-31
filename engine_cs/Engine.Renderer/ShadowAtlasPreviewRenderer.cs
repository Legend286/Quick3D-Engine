// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Numerics;
using Engine.CBindings;
using Engine.RHI;

namespace Engine.Renderer;

internal sealed class ShadowAtlasPreviewRenderer : IDisposable
{
    private readonly RhiDevice _device;
    private readonly RhiShader _vertexShader;
    private readonly RhiShader _fragmentShader;
    private readonly RhiPipeline _pipeline;
    private readonly RhiSampler _sampler;

    public ShadowAtlasPreviewRenderer(
        RhiDevice device,
        string contentRoot,
        Renderer renderer)
    {
        _device = device;
        string source = renderer.LoadShaderSource("shaders/shadow_atlas_preview.slang", contentRoot);
        _vertexShader = RhiShader.FromSource(
            device,
            source,
            "vertexMain",
            RhiNative.ShaderStage.Vertex,
            renderer.ActiveShaderIncludeDirs,
            renderer.ActiveShaderCliArgs);
        _fragmentShader = RhiShader.FromSource(
            device,
            source,
            "fragmentMain",
            RhiNative.ShaderStage.Fragment,
            renderer.ActiveShaderIncludeDirs,
            renderer.ActiveShaderCliArgs);
        _pipeline = RhiPipeline.CreateGraphics(
            device,
            _vertexShader,
            _fragmentShader,
            RhiNative.TextureFormat.Bgra8Unorm,
            enableDepth: false);
        _sampler = RhiSampler.Create(device);
    }

    public unsafe void Render(
        ShadowAtlasAllocation tile,
        RhiTexture target,
        uint width,
        uint height,
        RhiFence? syncFence,
        ulong waitValue,
        ulong signalValue)
    {
        using var recorder = new CommandRecorder(_device);
        if (syncFence != null && waitValue > 0)
            recorder.WaitFence(syncFence, waitValue);
        recorder.BeginRenderPass(
            target,
            RhiNative.LoadOp.Clear,
            RhiNative.StoreOp.Store);
        recorder.BindPipeline(_pipeline);
        recorder.SetViewport(0, 0, width, height);
        recorder.SetScissor(0, 0, width, height);
        recorder.BindTexture(0, tile.Texture);
        recorder.BindSampler(0, _sampler);
        float scale =
            tile.Size / (float)ShadowAtlas.PageSize;
        var uvScaleBias = new Vector4(
            scale,
            scale,
            tile.X / (float)ShadowAtlas.PageSize,
            tile.Y / (float)ShadowAtlas.PageSize);
        recorder.PushConstants(
            0,
            (uint)sizeof(Vector4),
            (IntPtr)(&uvScaleBias));
        recorder.Draw(3);
        recorder.EndPass();
        if (syncFence != null && signalValue > 0)
            recorder.SignalFence(syncFence, signalValue);
        recorder.Submit();
    }

    public void Dispose()
    {
        _sampler.Dispose();
        _pipeline.Dispose();
        _fragmentShader.Dispose();
        _vertexShader.Dispose();
    }
}
