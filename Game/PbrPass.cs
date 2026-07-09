// SPDX-License-Identifier: MIT
// PBR pass implementation.

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

public class PbrPass : RenderPass
{

    [StructLayout(LayoutKind.Sequential)]
    private struct PartData
    {
        public Vector4 AabbMin;
        public Vector4 AabbMax;
        public ulong Vertices;
        public ulong Indices;
        public uint IndexCount;
        public uint MaterialIdx;
        public uint InstanceIdx;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InstanceData
    {
        public Matrix4x4 ModelMatrix;
        public Vector4 AabbMin;
        public Vector4 AabbMax;
        public uint PartCount;
        public uint FirstPartIndex;
        public uint pad1;
        public uint pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MaterialData
    {
        public Vector4 BaseColor;
        public Vector4 EmissiveColor;
        public float Metallic;
        public float Roughness;
        public uint AlbedoTexIndex;
        public uint NormalTexIndex;
        public uint RmaTexIndex;
        public uint EmissiveTexIndex;
        public float Subsurface;
        public uint _pad0;
        public Vector4 SubsurfaceRadius;
        public Vector4 SubsurfaceColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LightData
    {
        public Vector4 Position;   // w = range
        public Vector4 Direction;  // w = type (0=Dir, 1=Point, 2=Spot)
        public Vector4 Color;      // w = intensity
        public Vector4 SpotParams; // x = innerCone, y = outerCone
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScenePushData
    {
        public ulong Parts;
        public ulong Instances;
        public ulong Materials;
        public ulong Camera;
        public ulong Lights;
        public uint LightCount;
        public uint FrameCount;
        public Vector2 Resolution;
        public uint DebugFlags;
        public uint pad_debug;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CameraData
    {
        public Matrix4x4 ViewProj;
        public Matrix4x4 InvViewProj;
        public Vector4 CameraPosition; // w = exposure
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CullPushData
    {
        public ulong Instances;
        public ulong Parts;
        public ulong DrawCmds;
        public ulong DrawCount;
        public Vector4 Plane0;
        public Vector4 Plane1;
        public Vector4 Plane2;
        public Vector4 Plane3;
        public Vector4 Plane4;
        public Vector4 Plane5;
        public uint InstanceCount;
        public uint pad1, pad2, pad3;
    }

    private readonly RhiDevice _device;
    private readonly IEntityStore _world;
    private readonly SceneGraph _scene;
    private readonly ScenePass _scenePass;
    private readonly string _contentRoot;

    private readonly RhiShader _vs;
    private readonly RhiShader _fs;
    private readonly RhiShader _cullCs;
    private readonly RhiPipeline _pipeline;
    private readonly RhiPipeline _cullPipeline;
    private readonly RhiSampler _sampler;
    private float _lastAspect;

    // Transient buffers for a frame
    private RhiBuffer _instanceBuffer;
    private RhiBuffer _partBuffer;
    private RhiBuffer _materialBuffer;
    private RhiBuffer _cameraBuffer;
    private RhiBuffer _lightBuffer;
    private RhiBuffer _drawCmdBuffer;
    private RhiBuffer _drawCountBuffer;

    private List<InstanceData> _instances = new();
    private List<PartData> _parts = new();
    private List<MaterialData> _materials = new();

    private RhiBindlessHeap _bindlessHeap;

    public unsafe PbrPass(RhiDevice device, IEntityStore world,
                              SceneGraph scene, ScenePass scenePass, string contentRoot, RhiBindlessHeap sharedHeap)
    {
        _device = device;
        _world = world;
        _scene = scene;
        _scenePass = scenePass;
        _contentRoot = contentRoot;
        Name = scenePass.Name;

        string shaderDir = Path.Combine(_contentRoot, "shaders");

        string src = LoadShaderSource("shaders/pbr.slang");
        _vs = RhiShader.FromSource(_device, src, "vertexMain", RhiNative.ShaderStage.Vertex, shaderDir);
        _fs = RhiShader.FromSource(_device, src, "fragmentMain", RhiNative.ShaderStage.Fragment, shaderDir);

        _pipeline = RhiPipeline.CreateGraphics(
            _device, _vs, _fs,
            RhiNative.TextureFormat.Bgra8Unorm,
            enableDepth: true);

        string cullSrc = LoadShaderSource("shaders/cull.slang");
        _cullCs = RhiShader.FromSource(_device, cullSrc, "computeMain", RhiNative.ShaderStage.Compute, shaderDir);
        _cullPipeline = RhiPipeline.CreateCompute(_device, _cullCs);

        _sampler = RhiSampler.Create(_device);

        // Preallocate buffers (in a real engine this would be a dynamic ring buffer)
        _instanceBuffer = RhiBuffer.Create(_device, 1024 * (ulong)sizeof(InstanceData), RhiNative.BufferUsage.Storage);
        _partBuffer = RhiBuffer.Create(_device, 4096 * (ulong)sizeof(PartData), RhiNative.BufferUsage.Storage);
        _materialBuffer = RhiBuffer.Create(_device, 1024 * (ulong)sizeof(MaterialData), RhiNative.BufferUsage.Storage);
        _cameraBuffer = RhiBuffer.Create(_device, (ulong)sizeof(CameraData), RhiNative.BufferUsage.Storage);
        _lightBuffer = RhiBuffer.Create(_device, 1024 * (ulong)sizeof(LightData), RhiNative.BufferUsage.Storage);

        // Indirect struct is 16 bytes
        _drawCmdBuffer = RhiBuffer.Create(_device, 4096 * 16, RhiNative.BufferUsage.Storage | RhiNative.BufferUsage.Indirect);
        _drawCountBuffer = RhiBuffer.Create(_device, 16, RhiNative.BufferUsage.Storage);

        _bindlessHeap = sharedHeap;
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Write(Engine.Game.Renderer.BackBufferHandle, ResourceState.RenderTarget);
        builder.Write(Engine.Game.Renderer.DepthBufferHandle, ResourceState.DepthStencil);
    }

    private void ExtractPlanes(Matrix4x4 vp, out CullPushData p)
    {
        p = default;
        // Left
        p.Plane0 = new Vector4(vp.M14 + vp.M11, vp.M24 + vp.M21, vp.M34 + vp.M31, vp.M44 + vp.M41);
        // Right
        p.Plane1 = new Vector4(vp.M14 - vp.M11, vp.M24 - vp.M21, vp.M34 - vp.M31, vp.M44 - vp.M41);
        // Bottom
        p.Plane2 = new Vector4(vp.M14 + vp.M12, vp.M24 + vp.M22, vp.M34 + vp.M32, vp.M44 + vp.M42);
        // Top
        p.Plane3 = new Vector4(vp.M14 - vp.M12, vp.M24 - vp.M22, vp.M34 - vp.M32, vp.M44 - vp.M42);
        // Near
        p.Plane4 = new Vector4(vp.M13, vp.M23, vp.M33, vp.M43);
        // Far
        p.Plane5 = new Vector4(vp.M14 - vp.M13, vp.M24 - vp.M23, vp.M34 - vp.M33, vp.M44 - vp.M43);

        float l0 = new Vector3(p.Plane0.X, p.Plane0.Y, p.Plane0.Z).Length(); p.Plane0 /= l0;
        float l1 = new Vector3(p.Plane1.X, p.Plane1.Y, p.Plane1.Z).Length(); p.Plane1 /= l1;
        float l2 = new Vector3(p.Plane2.X, p.Plane2.Y, p.Plane2.Z).Length(); p.Plane2 /= l2;
        float l3 = new Vector3(p.Plane3.X, p.Plane3.Y, p.Plane3.Z).Length(); p.Plane3 /= l3;
        float l4 = new Vector3(p.Plane4.X, p.Plane4.Y, p.Plane4.Z).Length(); p.Plane4 /= l4;
        float l5 = new Vector3(p.Plane5.X, p.Plane5.Y, p.Plane5.Z).Length(); p.Plane5 /= l5;
    }

    public override unsafe void Execute(ICommandSink sink, RenderGraphContext context)
    {
        if (!context.TryGetTexture(Engine.Game.Renderer.BackBufferHandle, out RhiTexture colorTarget))
            return;
        context.TryGetTexture(Engine.Game.Renderer.DepthBufferHandle, out RhiTexture depthTarget);

        uint w = context.Width > 0 ? context.Width : 1280;
        uint h = context.Height > 0 ? context.Height : 720;
        _lastAspect = (float)w / h;

        CameraData camData = default;
        camData.ViewProj = Matrix4x4.Identity;
        camData.CameraPosition = new Vector4(0, 0, 0, 1.0f); // 1.0f exposure default

        foreach (var id in _world.Entities)
        {
            if (_world.TryGet<Engine.Scene.Components.Camera>(id, out var cam))
            {
                var transform = _world.TryGet<Transform>(id, out var t) ? t : Transform.Default;
                var view = Matrix4x4.CreateLookAt(transform.Position, transform.Position + Vector3.Transform(Vector3.UnitZ, transform.Rotation), Vector3.UnitY);
                var proj = Matrix4x4.CreatePerspectiveFieldOfView(cam.FieldOfView, _lastAspect, cam.NearClip, cam.FarClip);
                camData.ViewProj = view * proj;
                Matrix4x4.Invert(camData.ViewProj, out Matrix4x4 invVP);
                camData.InvViewProj = invVP;
                camData.CameraPosition = new Vector4(transform.Position, 1.0f);
                break;
            }
        }

        if (camData.ViewProj == Matrix4x4.Identity)
        {
            camData.CameraPosition = new Vector4(0, 0, -5, 1.0f);
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, -5), Vector3.Zero, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(60.0f * (MathF.PI / 180.0f), _lastAspect, 0.1f, 100.0f);
            camData.ViewProj = view * proj;
            Matrix4x4.Invert(camData.ViewProj, out Matrix4x4 invVP2);
            camData.InvViewProj = invVP2;
        }

        _cameraBuffer.Upload(new ReadOnlySpan<CameraData>(ref camData));

        var lights = new List<LightData>();
        foreach (var l in _scene.Lights)
        {
            float type = 0.0f;
            if (l.Type == "point") type = 1.0f;
            else if (l.Type == "spot") type = 2.0f;

            lights.Add(new LightData
            {
                Position = new Vector4(l.Position[0], l.Position[1], l.Position[2], l.Range),
                Direction = new Vector4(l.Direction[0], l.Direction[1], l.Direction[2], type),
                Color = new Vector4(l.Color[0], l.Color[1], l.Color[2], l.Intensity),
                SpotParams = new Vector4(l.InnerCone, l.OuterCone, 0, 0)
            });
        }
        if (lights.Count == 0)
        {
            lights.Add(new LightData
            {
                Position = new Vector4(0, 0, 0, 10.0f),
                Direction = new Vector4(Vector3.Normalize(new Vector3(-1, 1, -1)), 0.0f), // Dir Light
                Color = new Vector4(1, 1, 1, 2.0f),
                SpotParams = Vector4.Zero
            });
        }
        _lightBuffer.Upload(CollectionsMarshal.AsSpan(lights));

        _instances.Clear();
        _parts.Clear();
        _materials.Clear();

        HashSet<Engine.Assets.Mesh> uniqueMeshes = new HashSet<Engine.Assets.Mesh>();

        uint GetTexIndex(RhiTexture? tex)
        {
            if (tex == null) return 0xFFFFFFFF;
            if (_bindlessHeap.TryLookup(tex, out uint idx)) return idx;
            return _bindlessHeap.Register(tex);
        }

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

                    Vector3 instAabbMin = new Vector3(float.MaxValue);
                    Vector3 instAabbMax = new Vector3(float.MinValue);

                    foreach (var p in model.Parts)
                    {
                        var mesh = AssetRegistry.GetMesh(p.MeshId);
                        var material = AssetRegistry.GetMaterial(p.MaterialId);

                        if (mesh == null) continue;
                        uniqueMeshes.Add(mesh);

                        uint matIdx = (uint)_materials.Count;
                        Vector4 baseColor = new Vector4(1, 1, 1, 1);
                        Vector4 emissiveColor = new Vector4(0, 0, 0, 1);
                        uint albedoTex = 0xFFFFFFFF;
                        uint normalTex = 0xFFFFFFFF;
                        uint rmaTex = 0xFFFFFFFF;

                        if (material != null)
                        {
                            if (material.AlbedoColor != null && material.AlbedoColor.Length >= 4)
                            {
                                baseColor = new Vector4(material.AlbedoColor[0], material.AlbedoColor[1], material.AlbedoColor[2], material.AlbedoColor[3]);
                            }
                            if (material.EmissiveColor != null && material.EmissiveColor.Length >= 4)
                            {
                                emissiveColor = new Vector4(material.EmissiveColor[0], material.EmissiveColor[1], material.EmissiveColor[2], material.EmissiveColor[3]);
                            }
                            albedoTex = GetTexIndex(material.AlbedoTexture);
                            normalTex = GetTexIndex(material.NormalTexture);
                            rmaTex = GetTexIndex(material.RmaTexture);
                        }
                        else
                        {
                            // Hardcoded fallback material (pink emissive)
                            baseColor = new Vector4(1.0f, 0.0f, 1.0f, 1.0f);
                            emissiveColor = new Vector4(1.0f, 0.0f, 1.0f, 1.0f);
                        }

                        _materials.Add(new MaterialData
                        {
                            BaseColor = baseColor,
                            EmissiveColor = emissiveColor,
                            Metallic = material?.Metallic ?? 0.0f,
                            Roughness = material?.Roughness ?? 1.0f,
                            AlbedoTexIndex = albedoTex,
                            NormalTexIndex = normalTex,
                            RmaTexIndex = rmaTex,
                            EmissiveTexIndex = 0xFFFFFFFF,
                            Subsurface = material?.Subsurface ?? 0.0f,
                            SubsurfaceRadius = Vector4.Zero,
                            SubsurfaceColor = Vector4.Zero,
                        });

                        // Use bounds loaded from the model
                        Vector3 partMin = p.BoundsMin;
                        Vector3 partMax = p.BoundsMax;

                        instAabbMin = Vector3.Min(instAabbMin, partMin);
                        instAabbMax = Vector3.Max(instAabbMax, partMax);

                        _parts.Add(new PartData
                        {
                            AabbMin = new Vector4(partMin, 1.0f),
                            AabbMax = new Vector4(partMax, 1.0f),
                            Vertices = mesh.VertexBuffer.DeviceAddress,
                            Indices = mesh.IndexBuffer.DeviceAddress,
                            IndexCount = mesh.IndexCount,
                            MaterialIdx = matIdx,
                            InstanceIdx = instIdx
                        });
                    }

                    if (_parts.Count > firstPart)
                    {
                        _instances.Add(new InstanceData
                        {
                            ModelMatrix = modelMatrix,
                            AabbMin = new Vector4(instAabbMin, 1.0f),
                            AabbMax = new Vector4(instAabbMax, 1.0f),
                            PartCount = (uint)(_parts.Count - firstPart),
                            FirstPartIndex = firstPart
                        });
                    }
                }
            }
        }

        if (_instances.Count > 0)
        {
            _instanceBuffer.Upload(CollectionsMarshal.AsSpan(_instances));
            _partBuffer.Upload(CollectionsMarshal.AsSpan(_parts));
            _materialBuffer.Upload(CollectionsMarshal.AsSpan(_materials));

            uint zero = 0;
            _drawCountBuffer.Upload(new ReadOnlySpan<uint>(ref zero));

            // 1. Dispatch Culling Compute
            ExtractPlanes(camData.ViewProj, out CullPushData cullPush);
            cullPush.Instances = _instanceBuffer.DeviceAddress;
            cullPush.Parts = _partBuffer.DeviceAddress;
            cullPush.DrawCmds = _drawCmdBuffer.DeviceAddress;
            cullPush.DrawCount = _drawCountBuffer.DeviceAddress;
            cullPush.InstanceCount = (uint)_instances.Count;

            sink.BeginComputePass();
            sink.BindPipeline(_cullPipeline);
            sink.UseBuffer(_instanceBuffer, 1);
            sink.UseBuffer(_partBuffer, 1);
            sink.UseBuffer(_drawCmdBuffer, 2); // Write usage: cull shader populates indirect draw commands
            sink.UseBuffer(_drawCountBuffer, 1);
            foreach (var mesh in uniqueMeshes)
            {
                sink.UseBuffer(mesh.VertexBuffer, 1);
                sink.UseBuffer(mesh.IndexBuffer, 1);
            }
            sink.PushConstants(0, (uint)sizeof(CullPushData), (IntPtr)(&cullPush));
            // 64 threads per group
            sink.Dispatch((uint)((_instances.Count + 63) / 64), 1, 1);
            sink.EndComputePass();

            // 2. Draw
            sink.BeginRenderPass(colorTarget, RhiNative.LoadOp.Clear, RhiNative.StoreOp.Store, depthTarget);
            sink.BindPipeline(_pipeline);
            sink.SetViewport(0, 0, w, h);
            sink.UseBuffer(_instanceBuffer, 1);
            sink.UseBuffer(_partBuffer, 1);
            sink.UseBuffer(_materialBuffer, 1);
            sink.UseBuffer(_cameraBuffer, 1);
            sink.UseBuffer(_lightBuffer, 1);
            foreach (var mesh in uniqueMeshes)
            {
                sink.UseBuffer(mesh.VertexBuffer, 1);
                sink.UseBuffer(mesh.IndexBuffer, 1);
            }

            ScenePushData pbrPush = new ScenePushData
            {
                Parts = _partBuffer.DeviceAddress,
                Instances = _instanceBuffer.DeviceAddress,
                Materials = _materialBuffer.DeviceAddress,
                Camera = _cameraBuffer.DeviceAddress,
                Lights = _lightBuffer.DeviceAddress,
                LightCount = (uint)lights.Count,
                FrameCount = 0,
                Resolution = new Vector2(w, h)
            };
            sink.PushConstants(0, (uint)sizeof(ScenePushData), (IntPtr)(&pbrPush));

            // Bind bindless textures
            if (_bindlessHeap.IsInitialized)
            {
                // Slot 1 for textures (auto-assigned buffer(1) by Slang Metal)
                sink.BindHeap(1, _bindlessHeap);
                // Slot 0 for sampler (auto-assigned sampler(0) by Slang Metal)
                sink.BindSampler(0, _sampler);
            }

            // Perform multidraw!
            // Wait, we need to read the draw count back to CPU or use draw count buffer.
            // Currently RHI doesn't support drawCountBuffer natively in Metal (requires ICB and manual looping).
            // But we can loop over maximum possible parts if we just read the drawCount!
            // In Metal, MTLIndirectCommandBuffer executes commands up to drawCount.
            // Wait, `MTLIndirectCommandBuffer` has `executeCommandsInBuffer:withRange:`.
            // Our RhiCmdDrawIndirect takes a fixed `draw_count`.
            // To make it fully GPU-driven, `draw_count` should be MAX_PARTS, and commands past drawCount
            // should have `vertexCount = 0` (or we add a GPU drawCount param to RHI).
            // Since we didn't add GPU drawCount to RHI, we can just dispatch ALL parts and let cull shader 
            // set vertexCount = 0 for culled parts!

            // So we don't even need atomic draw count for the indirect dispatch if we just dispatch _parts.Count
            // and cull shader uses partIdx directly to write into `drawCmds[partIdx]`.
            sink.DrawIndirect(_drawCmdBuffer, 0, (uint)_parts.Count, 16);

            sink.EndPass();
        }
        else
        {
            sink.BeginRenderPass(colorTarget, RhiNative.LoadOp.Clear, RhiNative.StoreOp.Store, depthTarget);
            sink.EndPass();
        }
    }

    private string LoadShaderSource(string relPath)
    {
        string full = Path.Combine(_contentRoot, relPath);
        if (!File.Exists(full)) throw new FileNotFoundException(full);
        return File.ReadAllText(full);
    }

    public void Dispose()
    {
        _pipeline?.Dispose();
        _vs?.Dispose();
        _fs?.Dispose();
        _cullPipeline?.Dispose();
        _cullCs?.Dispose();

        _instanceBuffer?.Dispose();
        _partBuffer?.Dispose();
        _materialBuffer?.Dispose();
        _cameraBuffer?.Dispose();
        _lightBuffer?.Dispose();
        _drawCmdBuffer?.Dispose();
        _drawCountBuffer?.Dispose();
        // _bindlessHeap is shared, owned by Renderer
    }
}
