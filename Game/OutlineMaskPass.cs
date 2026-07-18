using System;
using System.IO;
using System.Numerics;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.Scene;
using Engine.Scene.Components;
using Engine.CBindings;
using Engine.Assets;

namespace Engine.Game;

public sealed class OutlineMaskPass : RenderPass, IDisposable
{
    private readonly RhiDevice _device;
    private readonly IEntityStore _world;
    private readonly SceneGraph _scene;
    private readonly string _contentRoot;
    private readonly Renderer _renderer;

    private RhiShader _vs;
    private RhiShader _fs;
    private RhiPipeline? _pipeline;

    private RhiBuffer _instanceBuffer;
    private RhiBuffer _partBuffer;
    private RhiBuffer _cameraBuffer;

    private List<InstanceData> _instances = new();
    private List<PartData> _parts = new();

    public OutlineMaskPass(RhiDevice device, IEntityStore world, SceneGraph scene, string contentRoot, Renderer renderer)
    {
        _device = device;
        _world = world;
        _scene = scene;
        _contentRoot = contentRoot;
        _renderer = renderer;
        Name = "OutlineMaskPass";

        string shaderDir = Path.Combine(_contentRoot, "shaders");
        string src = File.ReadAllText(Path.Combine(shaderDir, "outline_mask.slang"));
        _vs = RhiShader.FromSource(_device, src, "vertexMain", RhiNative.ShaderStage.Vertex, shaderDir);
        _fs = RhiShader.FromSource(_device, src, "fragmentMain", RhiNative.ShaderStage.Fragment, shaderDir);

        _pipeline = RhiPipeline.CreateGraphics(
            _device, _vs, _fs,
            RhiNative.TextureFormat.Bgra8Unorm,
            enableDepth: true,
            enableBlend: false); 

        _instanceBuffer = RhiBuffer.Create(_device, (ulong)(Marshal.SizeOf<InstanceData>() * 1024), RhiNative.BufferUsage.Storage);
        _partBuffer = RhiBuffer.Create(_device, (ulong)(Marshal.SizeOf<PartData>() * 4096), RhiNative.BufferUsage.Storage);
        _cameraBuffer = RhiBuffer.Create(_device, (ulong)Marshal.SizeOf<CameraData>(), RhiNative.BufferUsage.Storage);
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Write(Engine.Game.Renderer.OutlineMaskHandle, ResourceState.RenderTarget);
        builder.Read(Engine.Game.Renderer.DepthBufferHandle, ResourceState.DepthStencil); // Read depth, do not write
    }

    public override unsafe void Execute(ICommandSink sink, RenderGraphContext context)
    {
        ulong selectedId = _renderer.SelectedEntity;
        if (selectedId == 0)
        {
            // Even if not drawn, we must clear the mask!
            if (context.TryGetTexture(Engine.Game.Renderer.OutlineMaskHandle, out RhiTexture emptyMask))
            {
                sink.BeginRenderPass(emptyMask, Engine.CBindings.RhiNative.LoadOp.Clear, Engine.CBindings.RhiNative.StoreOp.Store);
                sink.EndPass();
            }
            return;
        }

        if (!context.TryGetTexture(Engine.Game.Renderer.OutlineMaskHandle, out RhiTexture maskTarget)) return;
        if (!context.TryGetTexture(Engine.Game.Renderer.DepthBufferHandle, out RhiTexture depthTarget)) return;

        uint w = context.Width > 0 ? context.Width : 1280;
        uint h = context.Height > 0 ? context.Height : 720;
        float aspect = (float)w / h;

        CameraData camData = default;
        camData.ViewProj = Matrix4x4.Identity;
        foreach (var id in _world.Entities)
        {
            if (_world.TryGet<Engine.Scene.Components.Camera>(id, out var cam))
            {
                var transform = _world.TryGet<Transform>(id, out var t) ? t : Transform.Default;
                var view = Matrix4x4.CreateLookAt(transform.Position, transform.Position + Vector3.Transform(Vector3.UnitZ, transform.Rotation), Vector3.UnitY);
                var proj = Matrix4x4.CreatePerspectiveFieldOfView(cam.FieldOfView, aspect, cam.NearClip, cam.FarClip);
                camData.ViewProj = view * proj;
                Matrix4x4.Invert(camData.ViewProj, out Matrix4x4 invVP);
                camData.InvViewProj = invVP;
                camData.CameraPosition = new Vector4(transform.Position, 1.0f);
                break;
            }
        }

        if (camData.ViewProj == Matrix4x4.Identity)
        {
            // Use the same fallback camera as PbrPass
            camData.CameraPosition = new Vector4(0, 0, -5, 1.0f);
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, -5), Vector3.Zero, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(60.0f * (MathF.PI / 180.0f), aspect, 0.1f, 100.0f);
            camData.ViewProj = view * proj;
            Matrix4x4.Invert(camData.ViewProj, out Matrix4x4 invVP2);
            camData.InvViewProj = invVP2;
        }

        _cameraBuffer.Upload(new ReadOnlySpan<CameraData>(ref camData));

        _instances.Clear();
        _parts.Clear();

        HashSet<Engine.Assets.Mesh> uniqueMeshes = new HashSet<Engine.Assets.Mesh>();

        if (_world.TryGet<ModelComponent>(selectedId, out var mc))
        {
            var transform = _world.TryGet<Transform>(selectedId, out var t) ? t : Transform.Default;
            var modelMatrix = Matrix4x4.CreateScale(transform.Scale) *
                              Matrix4x4.CreateFromQuaternion(transform.Rotation) *
                              Matrix4x4.CreateTranslation(transform.Position);

            var model = AssetRegistry.GetModel(mc.ModelId);
            if (model != null && model.Parts != null)
            {
                uint instIdx = (uint)_instances.Count;
                uint firstPart = (uint)_parts.Count;

                foreach (var p in model.Parts)
                {
                    var mesh = AssetRegistry.GetMesh(p.MeshId);
                    if (mesh == null) continue;
                    uniqueMeshes.Add(mesh);

                    _parts.Add(new PartData
                    {
                        Vertices = mesh.VertexBuffer.DeviceAddress,
                        Indices = mesh.IndexBuffer.DeviceAddress,
                        IndexCount = mesh.IndexCount,
                        InstanceIdx = instIdx
                    });
                }

                if (_parts.Count > firstPart)
                {
                    _instances.Add(new InstanceData
                    {
                        ModelMatrix = modelMatrix,
                        PartCount = (uint)(_parts.Count - firstPart),
                        FirstPartIndex = firstPart,
                        pad1 = (uint)(selectedId & 0xFFFFFFFF),
                        pad2 = (uint)(selectedId >> 32)
                    });
                }
            }
        }

        if (_instances.Count == 0)
        {
            sink.BeginRenderPass(maskTarget, Engine.CBindings.RhiNative.LoadOp.Clear, Engine.CBindings.RhiNative.StoreOp.Store,
                                 depthTarget, Engine.CBindings.RhiNative.LoadOp.Load, Engine.CBindings.RhiNative.StoreOp.Store);
            sink.EndPass();
            return;
        }

        _instanceBuffer.Upload(CollectionsMarshal.AsSpan(_instances));
        _partBuffer.Upload(CollectionsMarshal.AsSpan(_parts));

        sink.BeginRenderPass(maskTarget, Engine.CBindings.RhiNative.LoadOp.Clear, Engine.CBindings.RhiNative.StoreOp.Store,
                             depthTarget, Engine.CBindings.RhiNative.LoadOp.Load, Engine.CBindings.RhiNative.StoreOp.Store);
        if (_pipeline != null) sink.BindPipeline(_pipeline);
        sink.SetViewport(0, 0, w, h);

        sink.UseBuffer(_instanceBuffer, 1);
        sink.UseBuffer(_partBuffer, 1);
        sink.UseBuffer(_cameraBuffer, 1);
        foreach (var mesh in uniqueMeshes)
        {
            sink.UseBuffer(mesh.VertexBuffer, 1);
            sink.UseBuffer(mesh.IndexBuffer, 1);
        }

        ScenePushData pushData = new ScenePushData
        {
            Parts = _partBuffer.DeviceAddress,
            Instances = _instanceBuffer.DeviceAddress,
            Camera = _cameraBuffer.DeviceAddress,
        };
        sink.PushConstants(0, (uint)sizeof(ScenePushData), new IntPtr(&pushData));

        using var drawCmdBuffer = RhiBuffer.Create(_device, (ulong)(_parts.Count * 16), RhiNative.BufferUsage.Storage | RhiNative.BufferUsage.Indirect);
        var cmds = new uint[_parts.Count * 4];
        for (int i = 0; i < _parts.Count; i++)
        {
            cmds[i * 4 + 0] = _parts[i].IndexCount;
            cmds[i * 4 + 1] = 1;
            cmds[i * 4 + 2] = 0;
            cmds[i * 4 + 3] = (uint)i; // baseInstance (acts as partIdx)
        }
        drawCmdBuffer.Upload(new ReadOnlySpan<uint>(cmds));

        sink.DrawIndirect(drawCmdBuffer, 0, (uint)_parts.Count, 16);

        sink.EndPass();
    }

    public void Dispose()
    {
        _pipeline?.Dispose();
        _vs.Dispose();
        _fs.Dispose();
        _instanceBuffer?.Dispose();
        _partBuffer?.Dispose();
        _cameraBuffer?.Dispose();
    }
}
