// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.Scene;
using Engine.Scene.Components;
using Engine.CBindings;
using Camera = Engine.Scene.Components.Camera;

namespace Engine.Game;

public sealed class GridPass : RenderPass, IDisposable
{
    private readonly RhiDevice _device;
    private readonly IEntityStore _world;
    private readonly string _contentRoot;
    private readonly Renderer _renderer;

    private RhiShader _vs;
    private RhiShader _fs;
    private RhiPipeline? _pipeline;
    private bool _clearScreen;
    private RhiBuffer? _vertexBuffer;
    private uint _vertexCount;

    [StructLayout(LayoutKind.Sequential)]
    private struct GridPushData
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 InvViewProj;
        public Vector4 CameraPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position;
    }

    public GridPass(RhiDevice device, IEntityStore world, string contentRoot, Renderer renderer, bool clearScreen = false)
    {
        _device = device;
        _world = world;
        _clearScreen = clearScreen;
        _contentRoot = contentRoot;
        _renderer = renderer;
        Name = "GridPass";

        string src = File.ReadAllText(Path.Combine(contentRoot, "shaders/grid.slang"));
        _vs = RhiShader.FromSource(_device, src, "vertexMain", RhiNative.ShaderStage.Vertex);
        _fs = RhiShader.FromSource(_device, src, "fragmentMain", RhiNative.ShaderStage.Fragment);

        _pipeline = RhiPipeline.CreateGraphics(
            _device, _vs, _fs,
            RhiNative.TextureFormat.Bgra8Unorm,
            enableDepth: true,
            enableDepthWrite: false,
            enableBlend: true,
            topology: RhiNative.PrimitiveTopology.LineList
        );

        GenerateGrid();
    }

    private unsafe void GenerateGrid()
    {
        int size = 100;
        int step = 1;
        var vertices = new System.Collections.Generic.List<Vertex>();

        for (int i = -size; i <= size; i += step)
        {
            vertices.Add(new Vertex { Position = new Vector3(i, 0, -size) });
            vertices.Add(new Vertex { Position = new Vector3(i, 0, size) });
            vertices.Add(new Vertex { Position = new Vector3(-size, 0, i) });
            vertices.Add(new Vertex { Position = new Vector3(size, 0, i) });
        }

        _vertexCount = (uint)vertices.Count;
        ulong bufferSize = (ulong)(_vertexCount * sizeof(Vertex));
        _vertexBuffer = RhiBuffer.Create(_device, bufferSize, RhiNative.BufferUsage.Vertex);

        fixed (Vertex* ptr = CollectionsMarshal.AsSpan(vertices))
        {
            _vertexBuffer.Upload((IntPtr)ptr, bufferSize);
        }
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Write(Renderer.BackBufferHandle, ResourceState.RenderTarget);
        builder.Write(Renderer.DepthBufferHandle, ResourceState.DepthStencil);
    }

    public override unsafe void Execute(ICommandSink sink, RenderGraphContext context)
    {
        if (!context.TryGetTexture(Renderer.BackBufferHandle, out RhiTexture colorTarget)) return;

        uint w = context.Width > 0 ? context.Width : 1280;
        uint h = context.Height > 0 ? context.Height : 720;
        float aspect = (float)w / h;

        bool foundCam = TryBuildCamera(
            _renderer.ActiveCameraEntity,
            aspect,
            out CameraData cameraData);
        for (ulong id = 1; !foundCam && id < 1024; ++id)
        {
            foundCam = TryBuildCamera(
                id,
                aspect,
                out cameraData);
        }

        if (!foundCam)
        {
            cameraData = _renderer.BuildCameraData(
                Camera.Default,
                Transform.Default with
                {
                    Position = new Vector3(0, 0, -5)
                },
                aspect,
                Vector3.UnitZ);
        }

        GridPushData pushData = new GridPushData
        {
            ViewProj = cameraData.ViewProj,
            InvViewProj = cameraData.InvViewProj,
            CameraPos = cameraData.CameraPosition
        };

        var loadOp = _clearScreen ? RhiNative.LoadOp.Clear : RhiNative.LoadOp.Load;
        context.TryGetTexture(Renderer.DepthBufferHandle, out RhiTexture depthTarget);
        if (_pipeline != null && _vertexBuffer != null)
        {
            var depthLoad = _clearScreen ? RhiNative.LoadOp.Clear : RhiNative.LoadOp.Load;
            sink.BeginRenderPass(colorTarget, loadOp, RhiNative.StoreOp.Store,
                                  depthTarget, depthLoad, RhiNative.StoreOp.Store);
            sink.SetViewport(0, 0, w, h);
            sink.BindPipeline(_pipeline);
            sink.BindVertexBuffer(1, _vertexBuffer, 0);
            sink.PushConstants(0, (uint)sizeof(GridPushData), new IntPtr(&pushData));
            sink.Draw(_vertexCount);
            sink.EndPass();
        }
    }

    private bool TryBuildCamera(
        ulong entity,
        float aspect,
        out CameraData cameraData)
    {
        cameraData = default;

        if (entity == 0 || !_world.TryGet<Engine.Scene.Components.Camera>(entity, out var cam))
            return false;

        var transform = _world.TryGet<Transform>(entity, out var t) ? t : Transform.Default;
        cameraData = _renderer.BuildCameraData(
            cam,
            transform,
            aspect,
            Vector3.UnitZ);
        return true;
    }

    public void Dispose()
    {
        _pipeline?.Dispose();
        _vs?.Dispose();
        _fs?.Dispose();
    }
}
