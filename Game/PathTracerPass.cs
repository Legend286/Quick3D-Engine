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
    private bool _hasGeometry;
    
    private readonly RhiDevice _device;
    private readonly IEntityStore _world;
    private readonly SceneGraph _scene;
    private readonly string _contentRoot;
    
    private uint _lastWidth = 0;
    private uint _lastHeight = 0;

    private RhiBuffer _cameraBuffer;
    private RhiBuffer _lightBuffer;
    private RhiBuffer _instanceBuffer;
    private RhiBuffer _partBuffer;
    private RhiBuffer _materialBuffer;
    
    private RhiBindlessHeap _bindlessHeap;

    /// <summary>When true, renders hit distance as grayscale instead of full path tracing.</summary>
    public static bool DebugMode = false;

    public PathTracerPass(RhiDevice device, IEntityStore world, SceneGraph scene, ScenePass scenePass, string contentRoot, RhiBindlessHeap sharedHeap)
    {
        Name = scenePass.Name;
        _device = device;
        _world = world;
        _scene = scene;
        _contentRoot = contentRoot;
        _bindlessHeap = sharedHeap;
        
        string shaderDir = Path.Combine(_contentRoot, "shaders");
        
        // DIAGNOSTIC: minimal test shader to validate compute pipeline
        string ptSrc = LoadShaderSource("shaders/minimal_test.slang");
        _computeShader = RhiShader.FromSource(_device, ptSrc, "computeMain", RhiNative.ShaderStage.Compute, shaderDir);
        _computePipeline = RhiPipeline.CreateCompute(_device, _computeShader);
        
        // DIAGNOSTIC: test blit shader that samples from a texture
        string blitSrc = LoadShaderSource("shaders/blit.slang");
        _blitVs = RhiShader.FromSource(_device, blitSrc, "vertexMain", RhiNative.ShaderStage.Vertex, shaderDir);
        _blitFs = RhiShader.FromSource(_device, blitSrc, "fragmentMain", RhiNative.ShaderStage.Fragment, shaderDir);
        _blitPipeline = RhiPipeline.CreateGraphics(_device, _blitVs, _blitFs, RhiNative.TextureFormat.Bgra8Unorm, enableDepth: false);
        _blitPipelineWithDepth = RhiPipeline.CreateGraphics(_device, _blitVs, _blitFs, RhiNative.TextureFormat.Bgra8Unorm, enableDepth: true);
        _blitSampler = RhiSampler.Create(_device);
        _computeSampler = RhiSampler.Create(_device);
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

        if (_outputBuffer == null || _lastWidth != w || _lastHeight != h)
        {
            _outputBuffer?.Dispose();
            _outputBuffer = RhiTexture.CreateStorage(_device, w, h, RhiNative.TextureFormat.Rgba16Float);
            _lastWidth = w;
            _lastHeight = h;
        }

        // DIAGNOSTIC: compute writes magenta → blit samples from _outputBuffer
        sink.BeginComputePass("Test Compute");
        sink.BindPipeline(_computePipeline);
        sink.BindTexture(4, _outputBuffer);
        sink.Dispatch((w + 63) / 64, h, 1, 64, 1, 1);
        sink.EndComputePass();

        ctx.TryGetTexture(Engine.Game.Renderer.DepthBufferHandle, out RhiTexture depthTarget);
        sink.BeginRenderPass(colorTarget, RhiNative.LoadOp.Clear, RhiNative.StoreOp.Store,
                              depthTarget, RhiNative.LoadOp.Clear, RhiNative.StoreOp.Store);
        sink.SetViewport(0, 0, w, h);
        sink.BindPipeline(_blitPipelineWithDepth);
        sink.BindTexture(1, _outputBuffer);
        sink.BindSampler(2, _blitSampler);
        sink.Draw(3);
        sink.EndPass();
    }

    private unsafe bool UpdateTlas(ICommandSink sink)
    {
        var instances = new List<RhiNative.TlasInstanceDesc>();
        var blasesToBuild = new List<RhiAccelStruct>();

        uint instanceId = 0;
        int hash = 0;
        foreach (var id in _world.Entities)
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
                        Flags = 0,
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

        // Build any new BLASes first (only needed once per mesh)
        if (blasesToBuild.Count > 0)
        {
            Log.Info($"[PathTracer] Building {blasesToBuild.Count} BLAS(es)", "PT");
            var span = CollectionsMarshal.AsSpan(blasesToBuild);
            sink.BuildAccelStructs(span);
        }

        // Compute a quick hash of the instance list to skip redundant TLAS rebuilds
        foreach (var inst in instances)
            hash = HashCode.Combine(hash, inst.InstanceId, inst.Blas.GetHashCode());

        bool hasAny = instances.Count > 0;
        if (hash == _lastInstanceHash && _tlas != null)
            return hasAny;

        _lastInstanceHash = hash;

        // Always create a TLAS even when there are zero instances.
        // A zero-instance TLAS never returns hits but keeps the Metal
        // command buffer valid — without a bound acceleration structure,
        // Slang's TraceRayInline silently faults the GPU, leaving the
        // output texture uninitialized (black).
        if (_tlas != null)
        {
            _tlas.Dispose();
            _tlas = null;
        }

        Log.Info($"[PathTracer] TLAS: {instances.Count} instances", "PT");
        var instArr = instances.ToArray();
        _tlas = RhiAccelStruct.CreateTlas(_device, new ReadOnlySpan<RhiNative.TlasInstanceDesc>(instArr));
        var tlasArr = new RhiAccelStruct[] { _tlas };
        sink.BuildAccelStructs(new ReadOnlySpan<RhiAccelStruct>(tlasArr));
        return hasAny;
    }
}
