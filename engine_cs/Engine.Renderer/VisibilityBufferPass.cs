// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.RenderGraph.Shaders;

namespace Engine.Renderer;

/// <summary>Rasterizes exact geometry identifiers and quantized barycentrics.</summary>
internal sealed class VisibilityBufferPass : RenderPass, IDisposable
{
    private readonly RasterSceneGpuCache _sceneCache;
    private readonly PbrPass _owner;
    private readonly RhiShader _vertexShader;
    private readonly RhiShader _fragmentShader;
    private readonly RhiPipeline _pipeline;
    private readonly RhiTexture[] _colorTargets = new RhiTexture[2];

    public VisibilityBufferPass(
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
        Name = "Visibility Buffer";
        IReadOnlyList<string> resolvedIncludeDirs =
            includeDirs ??
            new[] { Path.Combine(contentRoot, "shaders") };

        string source = LoadShaderSource(
            contentRoot,
            "visibility_buffer.slang",
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
        _pipeline = RhiPipeline.CreateGraphicsMrt(
            device,
            _vertexShader,
            _fragmentShader,
            RhiNative.TextureFormat.Rg32Uint,
            RhiNative.TextureFormat.Rg16Unorm,
            enableDepth: true,
            enableDepthWrite: true);
        _pipeline.SetDebugName(
            "Visibility Buffer Raster",
            "Visibility Buffer");
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Write(
            RenderGraphResources.VisibilityIdentifiersHandle,
            ResourceState.RenderTarget);
        builder.Write(
            RenderGraphResources.VisibilityBarycentricsHandle,
            ResourceState.RenderTarget);
        builder.Write(
            RenderGraphResources.DepthBufferHandle,
            ResourceState.DepthStencil);
        builder.Read(
            _owner.DrawCommandsHandle,
            ResourceState.ShaderRead);
    }

    public override unsafe void Execute(
        ICommandSink sink,
        RenderGraphContext context)
    {
        _owner.EnsurePrepared(sink, context);
        if (!context.TryGetTexture(
                RenderGraphResources.VisibilityIdentifiersHandle,
                out RhiTexture identifiers) ||
            !context.TryGetTexture(
                RenderGraphResources.VisibilityBarycentricsHandle,
                out RhiTexture barycentrics) ||
            !context.TryGetTexture(
                RenderGraphResources.DepthBufferHandle,
                out RhiTexture depth))
        {
            return;
        }

        SceneFrameData frameData = _owner.PreparedFrameData;
        ScenePushData pushData = _owner.PreparedPush;
        uint width = Math.Max(context.Width, 1u);
        uint height = Math.Max(context.Height, 1u);

        _colorTargets[0] = identifiers;
        _colorTargets[1] = barycentrics;
        sink.BeginRenderPass(
            _colorTargets,
            RhiNative.LoadOp.Clear,
            RhiNative.StoreOp.Store,
            depth,
            RhiNative.LoadOp.Clear,
            RhiNative.StoreOp.Store);
        sink.SetViewport(0, 0, width, height);

        if (frameData.Instances.Count > 0)
        {
            sink.BindPipeline(_pipeline);
            sink.UseBuffer(_sceneCache.InstanceBuffer, 1);
            sink.UseBuffer(_sceneCache.PartBuffer, 1);
            sink.UseBuffer(_sceneCache.CameraBuffer, 1);
            foreach (Engine.Assets.Mesh mesh in frameData.UniqueMeshes)
            {
                sink.UseBuffer(mesh.VertexBuffer, 1);
                sink.UseBuffer(mesh.IndexBuffer, 1);
            }
            sink.PushConstants(
                0,
                (uint)sizeof(ScenePushData),
                (IntPtr)(&pushData));
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
        _pipeline.Dispose();
        _fragmentShader.Dispose();
        _vertexShader.Dispose();
    }

    internal static string LoadShaderSource(
        string contentRoot,
        string fileName,
        IReadOnlyList<string> includeDirs)
    {
        string projectPath = Path.Combine(
            contentRoot,
            "shaders",
            fileName);
        if (File.Exists(projectPath))
            return File.ReadAllText(projectPath);

        foreach (string includeDir in includeDirs)
        {
            string includePath = Path.Combine(includeDir, fileName);
            if (File.Exists(includePath))
                return File.ReadAllText(includePath);
        }

        throw new FileNotFoundException(
            $"Shader '{fileName}' was not found in the project or active engine/plugin include paths.",
            projectPath);
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
