// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.Scene;
using Engine.Scene.Components;
using Engine.Assets;
using Engine.CBindings;

namespace Engine.Game;

public class IdPickingPass : RenderPass, IDisposable
{

    private readonly RhiDevice _device;
    private readonly IEntityStore _world;
    private readonly string _contentRoot;

    private readonly RhiShader _vs;
    private readonly RhiShader _fs;
    private readonly RhiPipeline _pipeline;

    private RhiBuffer _instanceBuffer;
    private RhiBuffer _partBuffer;
    private RhiBuffer _cameraBuffer;

    private List<InstanceData> _instances = new();
    private List<PartData> _parts = new();

    public uint PickX;
    public uint PickY;
    public bool PickRequested;
    public ulong PickedId = 0;
    public uint PickedPartIndex = 0;


    public unsafe IdPickingPass(RhiDevice device, IEntityStore world, string contentRoot)
    {
        _device = device;
        _world = world;
        _contentRoot = contentRoot;
        Name = "Id Picking";

        string shaderDir = Path.Combine(_contentRoot, "shaders");
        string src = File.ReadAllText(Path.Combine(shaderDir, "id_picking.slang"));
        _vs = RhiShader.FromSource(_device, src, "vertexMain", RhiNative.ShaderStage.Vertex, shaderDir);
        _fs = RhiShader.FromSource(_device, src, "fragmentMain", RhiNative.ShaderStage.Fragment, shaderDir);

        _pipeline = RhiPipeline.CreateGraphics(
            _device, _vs, _fs,
            RhiNative.TextureFormat.Rgba8Unorm,
            enableDepth: true);

        _instanceBuffer = RhiBuffer.Create(_device, 1024 * (ulong)sizeof(InstanceData), RhiNative.BufferUsage.Storage);
        _partBuffer = RhiBuffer.Create(_device, 4096 * (ulong)sizeof(PartData), RhiNative.BufferUsage.Storage);
        _cameraBuffer = RhiBuffer.Create(_device, (ulong)sizeof(CameraData), RhiNative.BufferUsage.Storage);
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        // We will execute this manually inside the pass, using transient textures
    }

    public override unsafe void Execute(ICommandSink sink, RenderGraphContext context)
    {
        if (!PickRequested) return;
        PickRequested = false;

        uint w = context.Width > 0 ? context.Width : 1280;
        uint h = context.Height > 0 ? context.Height : 720;
        float aspect = (float)w / h;

        CameraData camData = default;
        camData.ViewProj = Matrix4x4.Identity;

        foreach (var id in _world.Entities)
        {
            if (_world.TryGet<Engine.Scene.Components.Camera>(id, out var cam))
            {
                if (_world.TryGet<Transform>(id, out var t))
                {
                    var view = Matrix4x4.CreateLookAt(t.Position, t.Position + Vector3.Transform(-Vector3.UnitZ, t.Rotation), Vector3.UnitY);
                    Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(cam.FieldOfView, (float)w / h, cam.NearClip, cam.FarClip);
                    camData.ViewProj = view * proj;
                    camData.CameraPosition = new Vector4(t.Position, 1.0f);
                }
                break;
            }
        }

        if (camData.ViewProj == Matrix4x4.Identity) return;

        // Apply picking matrix transformation
        // We want the pixel at (PickX, PickY) to expand to the full 1x1 render target (-1 to 1 in NDC)
        // NDC x goes from -1 to 1, y from 1 to -1 (in Metal/Vulkan)
        // Actually, easiest way is to just render to a 1x1 viewport but we need to shift the projection matrix
        // Translation in NDC:
        float ndcX = ((PickX + 0.5f) / w) * 2.0f - 1.0f;
        float ndcY = -(((PickY + 0.5f) / h) * 2.0f - 1.0f); // Invert Y for NDC
        
        Matrix4x4 pickMatrix = Matrix4x4.CreateTranslation(-ndcX, -ndcY, 0) * Matrix4x4.CreateScale(w, h, 1.0f);
        camData.ViewProj = camData.ViewProj * pickMatrix;

        _cameraBuffer.Upload(new ReadOnlySpan<CameraData>(ref camData));

        _instances.Clear();
        _parts.Clear();

        HashSet<Engine.Assets.Mesh> uniqueMeshes = new HashSet<Engine.Assets.Mesh>();

        foreach (var id in _world.Entities)
        {
            if (_world.TryGet<ModelComponent>(id, out var modelComp))
            {
                var transform = _world.TryGet<Transform>(id, out var t) ? t : Transform.Default;
                var modelMatrix = Matrix4x4.CreateScale(transform.Scale) *
                                  Matrix4x4.CreateFromQuaternion(transform.Rotation) *
                                  Matrix4x4.CreateTranslation(transform.Position);

                var model = AssetRegistry.GetModel(modelComp.ModelId);
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
                            AabbMin = new Vector4(0f, 0f, 0f, 1f),
                            AabbMax = new Vector4(0f, 0f, 0f, 1f),
                            Vertices = mesh.VertexBuffer.DeviceAddress,
                            Indices = mesh.IndexBuffer.DeviceAddress,
                            IndexCount = mesh.IndexCount,
                            MaterialIdx = 0,
                            InstanceIdx = instIdx,
                            Flags = 0
                        });
                    }

                    if (_parts.Count > firstPart)
                    {
                        _instances.Add(new InstanceData
                        {
                            ModelMatrix = modelMatrix,
                            PartCount = (uint)(_parts.Count - firstPart),
                            FirstPartIndex = firstPart,
                            EntityIdLow = (uint)(id & 0xFFFFFFFF),
                            EntityIdHigh = (uint)(id >> 32)
                        });
                    }
                }
            }
        }

        if (_instances.Count == 0) return;

        _instanceBuffer.Upload(CollectionsMarshal.AsSpan(_instances));
        _partBuffer.Upload(CollectionsMarshal.AsSpan(_parts));

        using var pickTarget = RhiTexture.CreateRenderTarget(_device, 1, 1, RhiNative.TextureFormat.Rgba8Unorm);
        using var pickDepth = RhiTexture.CreateDepth(_device, 1, 1);

        sink.BeginRenderPass(pickTarget, RhiNative.LoadOp.Clear, RhiNative.StoreOp.Store, pickDepth);
        sink.BindPipeline(_pipeline);
        sink.SetViewport(0, 0, 1, 1);
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
            Parts = _partBuffer?.DeviceAddress ?? 0,
            Instances = _instanceBuffer?.DeviceAddress ?? 0,
            Materials = 0,
            Camera = _cameraBuffer.DeviceAddress,
            Lights = 0,
            LightCount = 0,
            FrameCount = 0,
            Resolution = new Vector4(w, h, 1.0f / w, 1.0f / h),
            DebugFlags = 0,
            HasGeometry = 1,
            pad0 = 0,
            pad1 = 0,
            Sky = new SkyParams()
        };
        sink.PushConstants(0, (uint)sizeof(ScenePushData), (IntPtr)(&pushData));

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
        sink.SubmitAndWait();

        var bytes = pickTarget.Readback(1, 1, 4);
        PickedId = (uint)bytes[0] | ((uint)bytes[1] << 8);
        PickedPartIndex = (uint)bytes[2] | ((uint)bytes[3] << 8);
    }


    public void Dispose()
    {
        _pipeline?.Dispose();
        _vs?.Dispose();
        _fs?.Dispose();

        _instanceBuffer?.Dispose();
        _partBuffer?.Dispose();
        _cameraBuffer?.Dispose();
    }
}
