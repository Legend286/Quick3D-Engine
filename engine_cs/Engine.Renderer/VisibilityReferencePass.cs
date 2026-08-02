// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.RenderGraph.Shaders;

namespace Engine.Renderer;

/// <summary>
/// Rasterizes attribute and full-PBR references for visibility validation.
/// </summary>
internal sealed class VisibilityReferencePass : RenderPass, IDisposable
{
    private readonly RasterSceneGpuCache _sceneCache;
    private readonly PbrPass _owner;
    private readonly RhiShader _vertexShader;
    private readonly RhiShader _fragmentShader;
    private readonly RhiPipeline _pipeline;
    private readonly RhiShader _pbrVertexShader;
    private readonly RhiShader _pbrFragmentShader;
    private readonly RhiPipeline _pbrPipeline;
    private readonly RhiShader _skyVertexShader;
    private readonly RhiShader _skyFragmentShader;
    private readonly RhiPipeline _skyPipeline;
    private readonly RhiSampler _sampler;

    public VisibilityReferencePass(
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
        Name = "Visibility Raster Reference";
        IReadOnlyList<string> resolvedIncludeDirs =
            includeDirs ??
            new[] { Path.Combine(contentRoot, "shaders") };
        string source = VisibilityBufferPass.LoadShaderSource(
            contentRoot,
            "visibility_reference.slang",
            resolvedIncludeDirs);
        _vertexShader = Compile(
            device,
            source,
            "vertexMain",
            RhiNative.ShaderStage.Vertex,
            cliArgs,
            resolvedIncludeDirs,
            compileCache);
        _fragmentShader = Compile(
            device,
            source,
            "fragmentMain",
            RhiNative.ShaderStage.Fragment,
            cliArgs,
            resolvedIncludeDirs,
            compileCache);
        _pipeline = RhiPipeline.CreateGraphics(
            device,
            _vertexShader,
            _fragmentShader,
            RhiNative.TextureFormat.Rgba16Float,
            enableDepth: true,
            enableDepthWrite: false);
        _sampler = RhiSampler.Create(device);
        _pipeline.SetDebugName(
            "Visibility Attribute Reference",
            "Visibility Buffer");

        string pbrSource = "#define VISIBILITY_REFERENCE 1\n" +
            VisibilityBufferPass.LoadShaderSource(
                contentRoot,
                "pbr.slang",
                resolvedIncludeDirs);
        _pbrVertexShader = Compile(
            device,
            pbrSource,
            "vertexMain",
            RhiNative.ShaderStage.Vertex,
            cliArgs,
            resolvedIncludeDirs,
            compileCache);
        _pbrFragmentShader = Compile(
            device,
            pbrSource,
            "fragmentMain",
            RhiNative.ShaderStage.Fragment,
            cliArgs,
            resolvedIncludeDirs,
            compileCache);
        _pbrPipeline = RhiPipeline.CreateGraphics(
            device,
            _pbrVertexShader,
            _pbrFragmentShader,
            RhiNative.TextureFormat.Rgba16Float,
            enableDepth: true,
            enableDepthWrite: false);
        _pbrPipeline.SetDebugName(
            "Visibility Forward PBR Reference",
            "Visibility Buffer");

        string skySource = "#define VISIBILITY_REFERENCE 1\n" +
            VisibilityBufferPass.LoadShaderSource(
                contentRoot,
                "pbr_sky.slang",
                resolvedIncludeDirs);
        _skyVertexShader = Compile(
            device,
            skySource,
            "vertexMain",
            RhiNative.ShaderStage.Vertex,
            cliArgs,
            resolvedIncludeDirs,
            compileCache);
        _skyFragmentShader = Compile(
            device,
            skySource,
            "fragmentMain",
            RhiNative.ShaderStage.Fragment,
            cliArgs,
            resolvedIncludeDirs,
            compileCache);
        _skyPipeline = RhiPipeline.CreateGraphics(
            device,
            _skyVertexShader,
            _skyFragmentShader,
            RhiNative.TextureFormat.Rgba16Float,
            enableDepth: true,
            enableDepthWrite: false);
        _skyPipeline.SetDebugName(
            "Visibility Sky Reference",
            "Visibility Buffer");
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Write(
            RenderGraphResources.VisibilityReferenceHandle,
            ResourceState.RenderTarget);
        builder.Read(
            RenderGraphResources.DepthBufferHandle,
            ResourceState.DepthStencil);
        builder.Read(
            _owner.DrawCommandsHandle,
            ResourceState.ShaderRead);
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
        bool pbrView = VisibilityShadingPass.IsComparisonView(debugView);
        if (!pbrView)
            return;
        if (!context.TryGetTexture(
                RenderGraphResources.VisibilityReferenceHandle,
                out RhiTexture reference) ||
            !context.TryGetTexture(
                RenderGraphResources.DepthBufferHandle,
                out RhiTexture depth))
        {
            return;
        }

        SceneFrameData frameData = _owner.PreparedFrameData;
        uint width = Math.Max(context.Width, 1u);
        uint height = Math.Max(context.Height, 1u);
        sink.BeginRenderPass(
            reference,
            RhiNative.LoadOp.Clear,
            RhiNative.StoreOp.Store,
            depth,
            RhiNative.LoadOp.Load,
            RhiNative.StoreOp.Store);
        sink.SetViewport(0, 0, width, height);
        sink.SetScissor(0, 0, width, height);
        if (pbrView)
        {
            _owner.BindShadingResources(sink);
            sink.PushConstants(
                0,
                (uint)sizeof(ScenePushData),
                (IntPtr)(&push));
            if (_owner.RenderSkyEnabled)
            {
                sink.BindPipeline(_skyPipeline);
                sink.Draw(3, 1, 0, 0);
            }
        }
        if (frameData.Instances.Count > 0)
        {
            if (!pbrView)
            {
                sink.UseBuffer(_sceneCache.InstanceBuffer, 1);
                sink.UseBuffer(_sceneCache.PartBuffer, 1);
                sink.UseBuffer(_sceneCache.MaterialBuffer, 1);
                sink.UseBuffer(_sceneCache.CameraBuffer, 1);
                foreach (Engine.Assets.Mesh mesh in frameData.UniqueMeshes)
                {
                    sink.UseBuffer(mesh.VertexBuffer, 1);
                    sink.UseBuffer(mesh.IndexBuffer, 1);
                }
                if (_owner.BindlessHeap.IsInitialized)
                {
                    sink.BindHeap(1, _owner.BindlessHeap);
                    sink.BindSampler(0, _sampler);
                }
            }
            sink.BindPipeline(pbrView ? _pbrPipeline : _pipeline);
            sink.PushConstants(
                0,
                (uint)sizeof(ScenePushData),
                (IntPtr)(&push));
            sink.DrawIndirect(
                _owner.DrawCommandBuffer,
                0,
                (uint)frameData.Parts.Count,
                16);
        }
        sink.EndPass();
    }

    public void Dispose()
    {
        _skyPipeline.Dispose();
        _skyFragmentShader.Dispose();
        _skyVertexShader.Dispose();
        _pbrPipeline.Dispose();
        _pbrFragmentShader.Dispose();
        _pbrVertexShader.Dispose();
        _sampler.Dispose();
        _pipeline.Dispose();
        _fragmentShader.Dispose();
        _vertexShader.Dispose();
    }

    private static RhiShader Compile(
        RhiDevice device,
        string source,
        string entryPoint,
        RhiNative.ShaderStage stage,
        IReadOnlyList<string>? cliArgs,
        IReadOnlyList<string>? includeDirs,
        ShaderCompileCache? compileCache)
    {
        if (compileCache == null)
        {
            return RhiShader.FromSource(
                device,
                source,
                entryPoint,
                stage,
                includeDirs,
                cliArgs);
        }
        return (RhiShader)compileCache.GetOrCompileHash(
            source,
            entryPoint,
            stage,
            includeDirs,
            cliArgs,
            () => RhiShader.FromSource(
                device,
                source,
                entryPoint,
                stage,
                includeDirs,
                cliArgs));
    }
}
