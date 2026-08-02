// SPDX-License-Identifier: MIT

using System;
using System.Numerics;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;

namespace Engine.Renderer;

/// <summary>Rasterizes selected-model depth for occluded outlines.</summary>
internal sealed class OutlineSelectionDepthPass : RenderPass, IDisposable
{
    private const ulong DrawCommandSize = 16;
    private readonly RhiDevice _device;
    private readonly RasterSceneGpuCache _sceneCache;
    private readonly Renderer _renderer;
    private readonly RhiShader _vertexShader;
    private readonly RhiShader _fragmentShader;
    private readonly RhiPipeline _pipeline;
    private RhiBuffer _drawCommands;
    private uint[] _commandWords = Array.Empty<uint>();

    internal OutlineSelectionDepthPass(
        RhiDevice device,
        string contentRoot,
        RasterSceneGpuCache sceneCache,
        Renderer renderer)
    {
        _device = device;
        _sceneCache = sceneCache;
        _renderer = renderer;
        Name = "Outline Selection Depth";
        string source = renderer.LoadShaderSource(
            "shaders/outline_selection_depth.slang",
            contentRoot);
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
        _pipeline = RhiPipeline.CreateDepthOnly(
            device,
            _vertexShader,
            _fragmentShader);
        _pipeline.SetDebugName(
            "Selected Instance Depth",
            "Editor Outline");
        _drawCommands = RhiBuffer.Create(
            device,
            DrawCommandSize,
            RhiNative.BufferUsage.Storage |
                RhiNative.BufferUsage.Indirect);
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Write(
            RenderGraphResources.OutlineSelectionDepthHandle,
            ResourceState.DepthStencil);
    }

    public override unsafe void Execute(
        ICommandSink sink,
        RenderGraphContext context)
    {
        ulong selectedEntity = _renderer.SelectedEntity;
        if (selectedEntity == 0 ||
            !context.TryGetTexture(
                RenderGraphResources.OutlineSelectionDepthHandle,
                out RhiTexture selectionDepth))
        {
            return;
        }

        uint width = Math.Max(context.Width, 1u);
        uint height = Math.Max(context.Height, 1u);
        _sceneCache.Prepare(
            context.FrameNumber,
            (float)width / height,
            width,
            height);
        SceneFrameData frameData = _sceneCache.FrameData;

        sink.BeginDepthOnlyPass(selectionDepth, RhiNative.LoadOp.Clear);
        int selectedInstanceIndex = FindSelectedInstance(
            frameData,
            selectedEntity);
        if (selectedInstanceIndex < 0)
        {
            sink.EndPass();
            return;
        }

        InstanceData instance =
            frameData.Instances[selectedInstanceIndex];
        int partCount = checked((int)instance.PartCount);
        EnsureDrawStorage(partCount);
        Span<uint> commands =
            _commandWords.AsSpan(0, partCount * 4);
        for (int drawIndex = 0; drawIndex < partCount; ++drawIndex)
        {
            uint partIndex = instance.FirstPartIndex + (uint)drawIndex;
            PartData part = frameData.Parts[(int)partIndex];
            int commandOffset = drawIndex * 4;
            commands[commandOffset] = part.IndexCount;
            commands[commandOffset + 1] = 1;
            commands[commandOffset + 2] = 0;
            commands[commandOffset + 3] = partIndex;
        }
        _drawCommands.Upload(commands);

        sink.SetViewport(0, 0, width, height);
        if (TryGetSelectionScissor(
                instance,
                frameData.Camera,
                width,
                height,
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
            sink.SetScissor(0, 0, width, height);
        }
        sink.BindPipeline(_pipeline);
        sink.UseBuffer(_sceneCache.InstanceBuffer, 1);
        sink.UseBuffer(_sceneCache.PartBuffer, 1);
        sink.UseBuffer(_sceneCache.CameraBuffer, 1);
        sink.UseBuffer(_drawCommands, 1);
        frameData.BindGeometry(sink);
        ScenePushData push = _sceneCache.PushData;
        sink.PushConstants(
            0,
            (uint)sizeof(ScenePushData),
            (IntPtr)(&push));
        sink.DrawIndirect(
            _drawCommands,
            0,
            (uint)partCount,
            (uint)DrawCommandSize);
        sink.EndPass();
    }

    public void Dispose()
    {
        _drawCommands.Dispose();
        _pipeline.Dispose();
        _fragmentShader.Dispose();
        _vertexShader.Dispose();
    }

    internal static int FindSelectedInstance(
        SceneFrameData frameData,
        ulong selectedEntity)
    {
        for (int instanceIndex = 0;
             instanceIndex < frameData.Instances.Count;
             ++instanceIndex)
        {
            InstanceData instance = frameData.Instances[instanceIndex];
            ulong entity =
                instance.EntityIdLow |
                ((ulong)instance.EntityIdHigh << 32);
            if (entity == selectedEntity)
                return instanceIndex;
        }
        return -1;
    }

    internal static bool TryGetSelectionScissor(
        InstanceData instance,
        CameraData camera,
        uint viewportWidth,
        uint viewportHeight,
        out uint x,
        out uint y,
        out uint width,
        out uint height)
    {
        Vector3 localMinimum = new(
            instance.AabbMin.X,
            instance.AabbMin.Y,
            instance.AabbMin.Z);
        Vector3 localMaximum = new(
            instance.AabbMax.X,
            instance.AabbMax.Y,
            instance.AabbMax.Z);
        Vector2 pixelMinimum = new(float.MaxValue);
        Vector2 pixelMaximum = new(float.MinValue);
        for (int cornerIndex = 0; cornerIndex < 8; ++cornerIndex)
        {
            Vector3 localCorner = new(
                (cornerIndex & 1) == 0
                    ? localMinimum.X
                    : localMaximum.X,
                (cornerIndex & 2) == 0
                    ? localMinimum.Y
                    : localMaximum.Y,
                (cornerIndex & 4) == 0
                    ? localMinimum.Z
                    : localMaximum.Z);
            Vector4 worldCorner = Vector4.Transform(
                new Vector4(localCorner, 1.0f),
                instance.ModelMatrix);
            Vector4 clipCorner = Vector4.Transform(
                worldCorner,
                camera.ViewProj);
            if (clipCorner.W <= 1e-5f)
            {
                x = y = 0;
                width = height = 0;
                return false;
            }
            Vector2 ndc = new(
                clipCorner.X / clipCorner.W,
                clipCorner.Y / clipCorner.W);
            Vector2 pixel = new(
                (ndc.X * 0.5f + 0.5f) * viewportWidth,
                (0.5f - ndc.Y * 0.5f) * viewportHeight);
            pixelMinimum = Vector2.Min(pixelMinimum, pixel);
            pixelMaximum = Vector2.Max(pixelMaximum, pixel);
        }

        const float padding = 5.0f;
        int minimumX = Math.Clamp(
            (int)MathF.Floor(pixelMinimum.X - padding),
            0,
            (int)viewportWidth);
        int minimumY = Math.Clamp(
            (int)MathF.Floor(pixelMinimum.Y - padding),
            0,
            (int)viewportHeight);
        int maximumX = Math.Clamp(
            (int)MathF.Ceiling(pixelMaximum.X + padding),
            0,
            (int)viewportWidth);
        int maximumY = Math.Clamp(
            (int)MathF.Ceiling(pixelMaximum.Y + padding),
            0,
            (int)viewportHeight);
        x = (uint)minimumX;
        y = (uint)minimumY;
        width = (uint)Math.Max(maximumX - minimumX, 0);
        height = (uint)Math.Max(maximumY - minimumY, 0);
        return width > 0 && height > 0;
    }

    private void EnsureDrawStorage(int partCount)
    {
        int wordCount = checked(partCount * 4);
        if (_commandWords.Length < wordCount)
            Array.Resize(ref _commandWords, wordCount);
        ulong requiredBytes =
            Math.Max((ulong)partCount * DrawCommandSize, DrawCommandSize);
        if (_drawCommands.Size >= requiredBytes)
            return;
        _drawCommands.Dispose();
        _drawCommands = RhiBuffer.Create(
            _device,
            requiredBytes,
            RhiNative.BufferUsage.Storage |
                RhiNative.BufferUsage.Indirect);
    }
}
