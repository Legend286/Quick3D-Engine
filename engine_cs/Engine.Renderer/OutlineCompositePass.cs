// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;

namespace Engine.Renderer;

internal sealed class OutlineCompositePass : RenderPass, IDisposable
{
    private readonly RasterSceneGpuCache _sceneCache;
    private readonly Renderer _renderer;

    private RhiShader _vs;
    private RhiShader _fs;
    private RhiPipeline? _pipeline;
    [StructLayout(LayoutKind.Sequential)]
    private struct CompositePushData
    {
        public ulong Parts;
        public ulong Instances;
        public uint PartCount;
        public uint SelectedEntityLow;
        public uint SelectedEntityHigh;
        public uint Width;
        public uint Height;
        public float DepthEpsilon;
        public float OutlineRadius;
        public uint Pad0;
    }

    internal OutlineCompositePass(
        RhiDevice device,
        string contentRoot,
        RasterSceneGpuCache sceneCache,
        Renderer renderer)
    {
        _sceneCache = sceneCache;
        _renderer = renderer;
        Name = "Instance ID Outline";

        string src = _renderer.LoadShaderSource(
            "shaders/outline_composite.slang",
            contentRoot);
        _vs = RhiShader.FromSource(
            device,
            src,
            "vertexMain",
            RhiNative.ShaderStage.Vertex,
            _renderer.ActiveShaderIncludeDirs,
            _renderer.ActiveShaderCliArgs);
        _fs = RhiShader.FromSource(
            device,
            src,
            "fragmentMain",
            RhiNative.ShaderStage.Fragment,
            _renderer.ActiveShaderIncludeDirs,
            _renderer.ActiveShaderCliArgs);
        _pipeline = RhiPipeline.CreateGraphics(
            device,
            _vs,
            _fs,
            RhiNative.TextureFormat.Bgra8Unorm,
            enableDepth: false,
            enableBlend: true);
        _pipeline.SetDebugName(
            "Instance ID Selection Outline",
            "Editor Outline");
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Read(
            RenderGraphResources.VisibilityIdentifiersHandle,
            ResourceState.ShaderRead);
        builder.Read(
            RenderGraphResources.DepthBufferHandle,
            ResourceState.ShaderRead);
        builder.Read(
            RenderGraphResources.OutlineSelectionDepthHandle,
            ResourceState.ShaderRead);
        builder.Write(RenderGraphResources.BackBufferHandle, ResourceState.RenderTarget);
    }

    public override unsafe void Execute(ICommandSink sink, RenderGraphContext context)
    {
        ulong selectedId = _renderer.SelectedEntity;
        if (selectedId == 0 ||
            !context.TryGetTexture(
                RenderGraphResources.BackBufferHandle,
                out RhiTexture backBuffer) ||
            !context.TryGetTexture(
                RenderGraphResources.VisibilityIdentifiersHandle,
                out RhiTexture identifiers) ||
            !context.TryGetTexture(
                RenderGraphResources.DepthBufferHandle,
                out RhiTexture sceneDepth) ||
            !context.TryGetTexture(
                RenderGraphResources.OutlineSelectionDepthHandle,
                out RhiTexture selectionDepth))
        {
            return;
        }

        uint w = context.Width > 0 ? context.Width : 1280;
        uint h = context.Height > 0 ? context.Height : 720;
        _sceneCache.Prepare(
            context.FrameNumber,
            (float)w / h,
            w,
            h);

        SceneFrameData frameData = _sceneCache.FrameData;
        int selectedInstanceIndex =
            OutlineSelectionDepthPass.FindSelectedInstance(
                frameData,
                selectedId);
        if (selectedInstanceIndex < 0)
            return;

        sink.BeginRenderPass(
            backBuffer,
            RhiNative.LoadOp.Load,
            RhiNative.StoreOp.Store);
        if (_pipeline != null)
            sink.BindPipeline(_pipeline);
        sink.SetViewport(0, 0, w, h);
        if (OutlineSelectionDepthPass.TryGetSelectionScissor(
                frameData.Instances[selectedInstanceIndex],
                frameData.Camera,
                w,
                h,
                out uint scissorX,
                out uint scissorY,
                out uint scissorWidth,
                out uint scissorHeight))
        {
            sink.SetScissor(
                scissorX,
                scissorY,
                scissorWidth,
                scissorHeight);
        }
        else
        {
            sink.SetScissor(0, 0, w, h);
        }

        var push = new CompositePushData
        {
            Parts = _sceneCache.PartBuffer.DeviceAddress,
            Instances = _sceneCache.InstanceBuffer.DeviceAddress,
            PartCount = (uint)frameData.Parts.Count,
            SelectedEntityLow = (uint)selectedId,
            SelectedEntityHigh = (uint)(selectedId >> 32),
            Width = w,
            Height = h,
            DepthEpsilon = 0.00035f,
            OutlineRadius = 2.8f,
        };

        sink.PushConstants(
            0,
            (uint)sizeof(CompositePushData),
            new IntPtr(&push));
        sink.UseBuffer(_sceneCache.PartBuffer, 1);
        sink.UseBuffer(_sceneCache.InstanceBuffer, 1);
        sink.BindTexture(0, identifiers);
        sink.BindTexture(1, sceneDepth);
        sink.BindTexture(2, selectionDepth);
        sink.Draw(3, 1, 0, 0);

        sink.EndPass();
    }

    public void Dispose()
    {
        _pipeline?.Dispose();
        _vs.Dispose();
        _fs.Dispose();
    }
}
