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

        SceneDataExtractor.Extract(
            _device,
            _world,
            _scene,
            _bindlessHeap,
            _lastAspect,
            ref _cameraBuffer,
            ref _lightBuffer,
            ref _instanceBuffer,
            ref _partBuffer,
            ref _materialBuffer,
            _renderer.ActiveCameraEntity,
            Vector3.UnitZ,
            _frameCount,
            DebugMode ? 1u : 0u,
            w,
            h,
            out SceneFrameData frameData,
            out ScenePushData pushData);

        _instances = frameData.Instances;
        _parts = frameData.Parts;
        _materials = frameData.Materials;

        if (frameData.Camera.ViewProj != _lastViewProj)
        {
            _frameCount = 0;
            _lastViewProj = frameData.Camera.ViewProj;
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
        pushData.FrameCount = _frameCount;
        pushData.HasGeometry = hasGeometry ? 1u : 0u;

        _frameCount++;

        sink.BeginComputePass("Path Tracer Compute");
        sink.BindPipeline(_computePipeline);

        sink.UseBuffer(_instanceBuffer, 1);
        sink.UseBuffer(_partBuffer, 1);
        sink.UseBuffer(_materialBuffer, 1);
        sink.UseBuffer(_cameraBuffer, 1);
        sink.UseBuffer(_lightBuffer, 1);
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
                        Flags = 5u,
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
            if (_oldTlasQueue.Count > 3) _oldTlasQueue.Dequeue().Dispose();
            return hasAny;
        }

        _lastInstanceHash = hash;
        _frameCount = 0;

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
