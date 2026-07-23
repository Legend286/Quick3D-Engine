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

public class PathTracerPass : RenderPass
{

    private RhiPipeline _computePipeline;
    private RhiShader _computeShader;
    private RhiPipeline _blitPipeline;
    private RhiPipeline _blitPipelineWithDepth;
    private RhiShader _blitVs;
    private RhiShader _blitFs;
    private RhiSampler _blitSampler;
    private RhiSampler _computeSampler;

    private RhiTexture _accumulationBuffer;
    private RhiTexture _outputBuffer;
    private RhiAccelStruct _tlas;
    private uint _frameCount;
    private int _lastInstanceHash;
    private int _lastMaterialHash;
    private Matrix4x4 _lastViewProj;
    private bool _hasGeometry;

    private readonly RhiDevice _device;
    private readonly IEntityStore _world;
    private readonly SceneGraph _scene;
    private readonly string _contentRoot;
    private readonly Engine.Game.Renderer _renderer;

    private uint _lastWidth = 0;
    private uint _lastHeight = 0;
    private float _lastAspect = 1.0f;

    private RhiBuffer _cameraBuffer;
    private RhiBuffer _lightBuffer;
    private RhiBuffer _instanceBuffer;
    private RhiBuffer _partBuffer;
    private RhiBuffer _materialBuffer;

    private List<InstanceData> _instances = new();
    private List<PartData> _parts = new();
    private List<MaterialData> _materials = new();

    private RhiBindlessHeap _bindlessHeap;

    /// <summary>When true, renders hit distance as grayscale instead of full path tracing.</summary>
    public static bool DebugMode = false;

    public unsafe PathTracerPass(RhiDevice device, IEntityStore world, SceneGraph scene, ScenePass scenePass, string contentRoot, RhiBindlessHeap sharedHeap, Engine.Game.Renderer renderer)
    {
        Name = scenePass.Name;
        _device = device;
        _world = world;
        _scene = scene;
        _contentRoot = contentRoot;
        _bindlessHeap = sharedHeap;
        _renderer = renderer;

        string shaderDir = Path.Combine(_contentRoot, "shaders");

        string ptSrc = LoadShaderSource("shaders/path_tracer.slang");
        _computeShader = RhiShader.FromSource(_device, ptSrc, "computeMain", RhiNative.ShaderStage.Compute, shaderDir);
        _computePipeline = RhiPipeline.CreateCompute(_device, _computeShader);

        string blitSrc = LoadShaderSource("shaders/blit.slang");
        _blitVs = RhiShader.FromSource(_device, blitSrc, "vertexMain", RhiNative.ShaderStage.Vertex, shaderDir);
        _blitFs = RhiShader.FromSource(_device, blitSrc, "fragmentMain", RhiNative.ShaderStage.Fragment, shaderDir);
        _blitPipeline = RhiPipeline.CreateGraphics(_device, _blitVs, _blitFs, RhiNative.TextureFormat.Bgra8Unorm, enableDepth: false);

        string blitDepthSrc = LoadShaderSource("shaders/blit_depth.slang");
        var blitDepthFs = RhiShader.FromSource(_device, blitDepthSrc, "fragmentMain", RhiNative.ShaderStage.Fragment, shaderDir);
        _blitPipelineWithDepth = RhiPipeline.CreateGraphics(_device, _blitVs, blitDepthFs, RhiNative.TextureFormat.Bgra8Unorm, enableDepth: true);
        _blitSampler = RhiSampler.Create(_device);
        _computeSampler = RhiSampler.Create(_device);

        _instanceBuffer = RhiBuffer.Create(_device, 16384 * (ulong)sizeof(InstanceData), RhiNative.BufferUsage.Storage);
        _partBuffer = RhiBuffer.Create(_device, 16384 * (ulong)sizeof(PartData), RhiNative.BufferUsage.Storage);
        _materialBuffer = RhiBuffer.Create(_device, 16384 * (ulong)sizeof(MaterialData), RhiNative.BufferUsage.Storage);
        _cameraBuffer = RhiBuffer.Create(_device, (ulong)sizeof(CameraData), RhiNative.BufferUsage.Storage);
        _lightBuffer = RhiBuffer.Create(_device, 16384 * (ulong)sizeof(LightData), RhiNative.BufferUsage.Storage);
    }

    private string LoadShaderSource(string relPath)
    {
        string full = Path.Combine(_contentRoot, relPath);
        if (!File.Exists(full)) throw new FileNotFoundException(full);
        return File.ReadAllText(full);
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Write(Engine.Game.Renderer.BackBufferHandle, ResourceState.RenderTarget);
        builder.Write(Engine.Game.Renderer.DepthBufferHandle, ResourceState.DepthStencil);
    }

    public override unsafe void Execute(ICommandSink sink, RenderGraphContext ctx)
    {
        if (!ctx.TryGetTexture(Engine.Game.Renderer.BackBufferHandle, out RhiTexture colorTarget))
            return;

        uint w = ctx.Width > 0 ? ctx.Width : 1280;
        uint h = ctx.Height > 0 ? ctx.Height : 720;
        _lastAspect = (float)w / h;

        if (_outputBuffer == null || _lastWidth != w || _lastHeight != h)
        {
            _outputBuffer?.Dispose();
            _accumulationBuffer?.Dispose();

            _outputBuffer = RhiTexture.CreateStorage(_device, w, h, RhiNative.TextureFormat.Rgba16Float);
            _accumulationBuffer = RhiTexture.CreateStorage(_device, w, h, RhiNative.TextureFormat.Rgba16Float);

            _lastWidth = w;
            _lastHeight = h;
            _frameCount = 0;
        }

        // --- POPULATE BUFFERS ---
        CameraData camData = default;
        camData.ViewProj = Matrix4x4.Identity;
        camData.CameraPosition = new Vector4(0, 0, 0, 1.0f); // 1.0f exposure default

        ulong activeCam = _renderer.ActiveCameraEntity;
        if (_world.TryGet<Engine.Scene.Components.Camera>(activeCam, out var cam))
        {
            var transform = _world.TryGet<Transform>(activeCam, out var t) ? t : Transform.Default;
            var forward = Vector3.Transform(Vector3.UnitZ, transform.Rotation);
            var view = Matrix4x4.CreateLookAt(transform.Position, transform.Position + forward, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(cam.FieldOfView, _lastAspect, cam.NearClip, cam.FarClip);
            camData.ViewProj = view * proj;
            Matrix4x4.Invert(camData.ViewProj, out Matrix4x4 invVP);
            camData.InvViewProj = invVP;
            camData.CameraPosition = new Vector4(transform.Position, 1.0f);
            camData.CameraForward = new Vector4(forward, 0.0f);
        }

        if (camData.ViewProj == Matrix4x4.Identity)
        {
            camData.CameraPosition = new Vector4(0, 0, -5, 1.0f);
            camData.CameraForward = new Vector4(0, 0, 1, 0.0f);
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, -5), Vector3.Zero, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(60.0f * (MathF.PI / 180.0f), _lastAspect, 0.1f, 100.0f);
            camData.ViewProj = view * proj;
            Matrix4x4.Invert(camData.ViewProj, out Matrix4x4 invVP2);
            camData.InvViewProj = invVP2;
        }

        if (camData.ViewProj != _lastViewProj)
        {
            _frameCount = 0;
            _lastViewProj = camData.ViewProj;
        }

        _cameraBuffer.Upload(new ReadOnlySpan<CameraData>(ref camData));

        var lights = new List<LightData>();
        Vector3 skySunDir = Vector3.Normalize(new Vector3(0.5f, 1.0f, 0.5f));
        float skySunRadius = 0.00465f;

        foreach (var l in _scene.Lights)
        {
            float type = 0.0f;
            float p1 = l.InnerCone;
            float p2 = l.OuterCone;
            if (l.Type == "point") type = 1.0f;
            else if (l.Type == "spot") type = 2.0f;
            else if (l.Type == "directional")
            {
                type = 0.0f;
                p1 = l.SunRadius;
                skySunDir = Vector3.Normalize(new Vector3(-l.Direction[0], -l.Direction[1], -l.Direction[2]));
                skySunRadius = l.SunRadius;
            }

            lights.Add(new LightData
            {
                Position = new Vector4(l.Position[0], l.Position[1], l.Position[2], l.Range),
                Direction = new Vector4(l.Direction[0], l.Direction[1], l.Direction[2], type),
                Color = new Vector4(l.Color[0], l.Color[1], l.Color[2], l.Intensity),
                SpotParams = new Vector4(p1, p2, 0, 0)
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



        uint GetTexIndex(RhiTexture tex)
        {
            if (tex == null) return 0xFFFFFFFF;
            if (_bindlessHeap.TryLookup(tex, out uint idx)) return idx;
            return _bindlessHeap.Register(tex);
        }

        var sortedEntities = new List<ulong>(_world.Entities);
        sortedEntities.Sort();

        foreach (var id in sortedEntities)
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

                        uint matIdx = (uint)_materials.Count;
                        Vector4 baseColor = new Vector4(1, 1, 1, 1);
                        Vector4 emissiveColor = new Vector4(0, 0, 0, 1);
                        uint albedoTex = 0xFFFFFFFF;
                        uint normalTex = 0xFFFFFFFF;
                        uint rmaTex = 0xFFFFFFFF;
                        uint emissiveTex = 0xFFFFFFFF;

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
                            emissiveTex = 0xFFFFFFFF;
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
                            EmissiveTexIndex = emissiveTex,
                            Subsurface = material?.Subsurface ?? 0.0f,
                            SubsurfaceColor = material?.SubsurfaceColor != null && material.SubsurfaceColor.Length >= 3 ? new Vector4(material.SubsurfaceColor[0], material.SubsurfaceColor[1], material.SubsurfaceColor[2], 0) : Vector4.Zero,
                            SubsurfaceRadius = material?.SubsurfaceRadius != null && material.SubsurfaceRadius.Length >= 3 ? new Vector4(material.SubsurfaceRadius[0], material.SubsurfaceRadius[1], material.SubsurfaceRadius[2], 0) : Vector4.Zero,
                            TopColor = material?.TopColor != null && material.TopColor.Length >= 4 ? new Vector4(material.TopColor[0], material.TopColor[1], material.TopColor[2], material.TopColor[3]) : Vector4.One,
                            TopMetallic = material?.TopMetallic ?? 0.0f,
                            TopRoughness = material?.TopRoughness ?? 1.0f,
                            TopMaskType = material?.TopMaskType ?? 0,
                            TopMaskTexIndex = GetTexIndex(material?.TopMaskTexture),
                            Layer2Color = material?.Layer2Color != null && material.Layer2Color.Length >= 4 ? new Vector4(material.Layer2Color[0], material.Layer2Color[1], material.Layer2Color[2], material.Layer2Color[3]) : Vector4.One,
                            Layer2Metallic = material?.Layer2Metallic ?? 0.0f,
                            Layer2Roughness = material?.Layer2Roughness ?? 1.0f,
                            Layer2MaskType = material?.Layer2MaskType ?? 0,
                            Layer2MaskTexIndex = GetTexIndex(material?.Layer2MaskTexture),
                            Clearcoat = material?.Clearcoat ?? 0.0f,
                            ClearcoatRoughness = material?.ClearcoatRoughness ?? 1.0f,
                            NoiseScale = material?.NoiseScale ?? 10.0f,
                            NoiseThresholdMin = material?.NoiseThresholdMin ?? 0.3f,
                            NoiseThresholdMax = material?.NoiseThresholdMax ?? 0.7f,
                            Layer2NoiseScale = material?.Layer2NoiseScale ?? 10.0f,
                            Layer2NoiseThresholdMin = material?.Layer2NoiseThresholdMin ?? 0.3f,
                            Layer2NoiseThresholdMax = material?.Layer2NoiseThresholdMax ?? 0.7f
                        });

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
                            InstanceIdx = instIdx,
                            Flags = mesh.IndexFormat == 32 ? 1u : 0u
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
                            FirstPartIndex = firstPart,
                            EntityIdLow = (uint)(id & 0xFFFFFFFF),
                            EntityIdHigh = (uint)(id >> 32)
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
        }

        int currentMatHash = 0;
        foreach (var m in _materials)
        {
            currentMatHash = HashCode.Combine(currentMatHash, 
                m.BaseColor.GetHashCode(), m.Metallic.GetHashCode(), m.Roughness.GetHashCode(), m.Subsurface.GetHashCode(), m.SubsurfaceColor.GetHashCode(), m.SubsurfaceRadius.GetHashCode());
        }
        if (currentMatHash != _lastMaterialHash)
        {
            _lastMaterialHash = currentMatHash;
            _frameCount = 0;
        }

        bool hasGeometry = UpdateTlas(sink);

        ScenePushData pushData = new ScenePushData
        {
            Parts = _partBuffer.DeviceAddress,
            Instances = _instanceBuffer.DeviceAddress,
            Materials = _materialBuffer.DeviceAddress,
            Camera = _cameraBuffer.DeviceAddress,
            Lights = _lightBuffer.DeviceAddress,
            LightCount = (uint)lights.Count,
            FrameCount = _frameCount,
            Resolution = new Vector4(w, h, 1.0f / w, 1.0f / h),
            DebugFlags = DebugMode ? 1u : 0u,
            HasGeometry = hasGeometry ? 1u : 0u,
            pad0 = 0,
            pad1 = 0,
            Sky = new SkyParams
            {
                SunDirAndRadius = new Vector4(skySunDir, skySunRadius),
                IntensityTurbidityAlbedoPad = new Vector4(1.0f, 2.0f, 0.1f, 0.0f)
            }
        };

        _frameCount++;

        // --- PATH TRACING COMPUTE PASS ---
        sink.BeginComputePass("Path Tracer Compute");
        sink.BindPipeline(_computePipeline);

        sink.UseBuffer(_instanceBuffer, 1);
        sink.UseBuffer(_partBuffer, 1);
        sink.UseBuffer(_materialBuffer, 1);
        sink.UseBuffer(_cameraBuffer, 1);
        sink.UseBuffer(_lightBuffer, 1);
        // Mesh BLAS, Vertex, and Index buffers are automatically made resident by the C++ backend
        // when the TLAS is used.

        if (_bindlessHeap.IsInitialized)
        {
            sink.BindHeap(1, _bindlessHeap);
            sink.BindSampler(0, _computeSampler);
        }

        sink.BindTexture(0, _accumulationBuffer);
        sink.BindTexture(1, _outputBuffer);

        if (_tlas != null)
        {
            sink.BindAccelStruct(2, _tlas);
            sink.UseAccelStruct(_tlas, 1);
        }

        sink.PushConstants(0, (uint)sizeof(ScenePushData), (IntPtr)(&pushData));
        sink.Dispatch((w + 63) / 64, h, 1, 64, 1, 1);
        sink.EndComputePass();

        // --- BLIT TO SCREEN ---
        ctx.TryGetTexture(Engine.Game.Renderer.DepthBufferHandle, out RhiTexture depthTarget);
        sink.BeginRenderPass(colorTarget, RhiNative.LoadOp.Clear, RhiNative.StoreOp.Store,
                              depthTarget, RhiNative.LoadOp.Clear, RhiNative.StoreOp.Store);
        sink.SetViewport(0, 0, w, h);
        if (depthTarget != null)
        {
            sink.BindPipeline(_blitPipelineWithDepth);
        }
        else
        {
            sink.BindPipeline(_blitPipeline);
        }
        sink.BindTexture(0, _outputBuffer);
        sink.BindSampler(0, _blitSampler);
        sink.Draw(3);
        sink.EndPass();
    }

    private Queue<RhiAccelStruct> _oldTlasQueue = new Queue<RhiAccelStruct>();

    private unsafe bool UpdateTlas(ICommandSink sink)
    {
        var instances = new List<RhiNative.TlasInstanceDesc>();
        var blasesToBuild = new List<RhiAccelStruct>();

        uint instanceId = 0;
        int hash = 0;

        var validEntities = new List<ulong>(_world.Entities);
        validEntities.Sort();

        foreach (var id in validEntities)
        {
            if (_world.TryGet<ModelComponent>(id, out var mc))
            {
                var tc = _world.TryGet<Transform>(id, out var t) ? t : Transform.Default;

                var model = AssetRegistry.GetModel(mc.ModelId);
                if (model == null || model.Parts == null) continue;

                foreach (var p in model.Parts)
                {
                    var mesh = AssetRegistry.GetMesh(p.MeshId);
                    if (mesh == null) continue;

                    if (mesh.Blas == null)
                    {
                        var geom = new RhiNative.BlasGeometryDesc
                        {
                            VertexBuffer = mesh.VertexBuffer.Handle,
                            VertexBufferOffset = 0,
                            VertexStride = (uint)sizeof(Engine.Assets.Vertex),
                            VertexCount = mesh.VertexCount,
                            VertexFormat = RhiNative.VertexFormat.Float3,
                            IndexBuffer = mesh.IndexBuffer.Handle,
                            IndexBufferOffset = 0,
                            IndexCount = mesh.IndexCount,
                            Is32BitIndex = mesh.IndexFormat == 32 ? 1 : 0
                        };

                        var bDesc = new RhiNative.AccelStructDesc
                        {
                            Abi = 6,
                            Type = RhiNative.AccelStructType.Blas,
                            Geometries = (IntPtr)(&geom),
                            GeometryCount = 1
                        };

                        mesh.Blas = RhiAccelStruct.Create(_device, in bDesc);
                        blasesToBuild.Add(mesh.Blas);
                        Log.Info($"[PathTracer] BLAS built: mesh={mesh.VertexCount}v/{mesh.IndexCount}i 32bit={mesh.IndexFormat == 32}", "PT");
                    }

                    var modelMat = Matrix4x4.CreateScale(tc.Scale) *
                                   Matrix4x4.CreateFromQuaternion(tc.Rotation) *
                                   Matrix4x4.CreateTranslation(tc.Position);

                    var inst = new RhiNative.TlasInstanceDesc
                    {
                        InstanceId = instanceId,
                        Mask = 0xFF,
                        InstanceOffset = 0,
                        Flags = 5u, // 1 (DisableTriangleCulling) | 4 (Opaque)
                        Blas = mesh.Blas.Handle
                    };

                    inst.Transform[0] = modelMat.M11; inst.Transform[1] = modelMat.M21; inst.Transform[2] = modelMat.M31; inst.Transform[3] = modelMat.M41;
                    inst.Transform[4] = modelMat.M12; inst.Transform[5] = modelMat.M22; inst.Transform[6] = modelMat.M32; inst.Transform[7] = modelMat.M42;
                    inst.Transform[8] = modelMat.M13; inst.Transform[9] = modelMat.M23; inst.Transform[10] = modelMat.M33; inst.Transform[11] = modelMat.M43;

                    instances.Add(inst);
                    instanceId++;
                }
            }
        }

        if (blasesToBuild.Count > 0)
        {
            Log.Info($"[PathTracer] Building {blasesToBuild.Count} BLAS(es)", "PT");
            var span = CollectionsMarshal.AsSpan(blasesToBuild);
            sink.BuildAccelStructs(span);
        }

        foreach (var inst in instances)
        {
            hash = HashCode.Combine(hash, inst.InstanceId, inst.Blas.GetHashCode());
            for (int i = 0; i < 12; i++)
                hash = HashCode.Combine(hash, inst.Transform[i].GetHashCode());
        }

        bool hasAny = instances.Count > 0;
        if (hash == _lastInstanceHash && _tlas != null)
        {
            // Keep queue cleaned up even when not rebuilding
            if (_oldTlasQueue.Count > 3) _oldTlasQueue.Dequeue().Dispose();
            return hasAny;
        }

        _lastInstanceHash = hash;
        _frameCount = 0; // Reset accumulation when geometry or transforms change

        if (_tlas != null)
        {
            _oldTlasQueue.Enqueue(_tlas);
            if (_oldTlasQueue.Count > 3) _oldTlasQueue.Dequeue().Dispose();
            _tlas = null;
        }

        if (hasAny)
        {
            Log.Info($"[PathTracer] TLAS: {instances.Count} instances", "PT");
            var instArr = instances.ToArray();
            _tlas = RhiAccelStruct.CreateTlas(_device, new ReadOnlySpan<RhiNative.TlasInstanceDesc>(instArr));
            var tlasArr = new RhiAccelStruct[] { _tlas };
            sink.BuildAccelStructs(new ReadOnlySpan<RhiAccelStruct>(tlasArr));
        }
        return hasAny;
    }

    public void Dispose()
    {
        _computePipeline?.Dispose();
        _computeShader?.Dispose();
        _blitPipeline?.Dispose();
        _blitPipelineWithDepth?.Dispose();
        _blitVs?.Dispose();
        _blitFs?.Dispose();
        _blitSampler?.Dispose();
        _computeSampler?.Dispose();
        _accumulationBuffer?.Dispose();
        _outputBuffer?.Dispose();
        _tlas?.Dispose();
        _instanceBuffer?.Dispose();
        _partBuffer?.Dispose();
        _materialBuffer?.Dispose();
        _cameraBuffer?.Dispose();
        _lightBuffer?.Dispose();
    }
}
