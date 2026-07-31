// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Numerics;
using System.Collections.Generic;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.Scene;
using Engine.Scene.Components;
using Engine.Assets;
using Engine.CBindings;

namespace Engine.Renderer;

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
    private RhiAccelStruct? _tlas;
    private uint _frameCount;
    private int _lastMaterialHash;
    private Matrix4x4 _lastViewProj;

    private readonly RhiDevice _device;
    private readonly IEntityStore _world;
    private readonly SceneGraph _scene;
    private readonly string _contentRoot;
    private readonly Renderer _renderer;
    private readonly RaytracingSceneCache _sceneCache;

    private uint _lastWidth = 0;
    private uint _lastHeight = 0;
    private float _lastAspect = 1.0f;
    private uint _lastDebugFlags = uint.MaxValue;

    private RhiBuffer _cameraBuffer;
    private RhiBuffer _lightBuffer;
    private RhiBuffer _instanceBuffer;
    private RhiBuffer _partBuffer;
    private RhiBuffer _materialBuffer;

    private List<InstanceData> _instances = new();
    private List<PartData> _parts = new();
    private List<MaterialData> _materials = new();

    private RhiBindlessHeap _bindlessHeap;

    public unsafe PathTracerPass(RhiDevice device, IEntityStore world, SceneGraph scene, ScenePass scenePass, string contentRoot, RhiBindlessHeap sharedHeap, Renderer renderer)
    {
        Name = string.IsNullOrWhiteSpace(scenePass.Name) ||
            scenePass.Name.Equals("PbrPass", StringComparison.OrdinalIgnoreCase)
            ? "Path Tracing"
            : $"Path Tracing · {scenePass.Name}";
        _device = device;
        _world = world;
        _scene = scene;
        _contentRoot = contentRoot;
        _renderer = renderer;
        _bindlessHeap = sharedHeap;
        _sceneCache = new RaytracingSceneCache(device, world);

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
        builder.Write(Renderer.BackBufferHandle, ResourceState.RenderTarget);
        builder.Write(Renderer.DepthBufferHandle, ResourceState.DepthStencil);
    }

    public override unsafe void Execute(ICommandSink sink, RenderGraphContext ctx)
    {
        if (!ctx.TryGetTexture(Renderer.BackBufferHandle, out RhiTexture colorTarget))
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
            _renderer.DebugFlags,
            _renderer.ProjectionBlend,
            _renderer.OrthographicSize,
            w,
            h,
            out SceneFrameData frameData,
            out ScenePushData pushData);

        if (_lastDebugFlags != pushData.DebugFlags)
        {
            _lastDebugFlags = pushData.DebugFlags;
            _frameCount = 0;
        }

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

        // Shared scene-mesh raytracing cache rebuilds BLAS on first
        // touch of a mesh and the TLAS only when instance hash drifts.
        // The raster pipeline never enters this code-path; pure pass
        // gating is enforced by the plugin host (no consumer = no cache).
        RaytracingSceneCache.TlasUpdateResult tlasInfo =
            _sceneCache.TryUpdateTlas(sink);
        _tlas = tlasInfo.SceneTlas;
        bool hasGeometry = tlasInfo.HasGeometry;
        if (tlasInfo.TopologyChanged)
            _frameCount = 0;

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

        ctx.TryGetTexture(Renderer.DepthBufferHandle, out RhiTexture depthTarget);
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
        _instanceBuffer?.Dispose();
        _partBuffer?.Dispose();
        _materialBuffer?.Dispose();
        _cameraBuffer?.Dispose();
        _lightBuffer?.Dispose();
        _sceneCache?.Dispose();
    }
}
