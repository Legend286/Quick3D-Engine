// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.Scene;
using Engine.CBindings;

namespace Engine.Game;

public class PbrPass : RenderPass
{
    private const ulong DrawIndirectCommandSizeBytes = 16;
    private const ulong DrawCountBufferSizeBytes = 16;
    private const uint ClusterTileSize = 32;
    private const uint ClusterDepthSlices = 16;
    private const uint MaxLightsPerCluster = 64;

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
    private readonly string _contentRoot;
    private readonly Engine.Game.Renderer _renderer;
    private readonly bool _renderSky;

    private readonly RhiShader _vs;
    private readonly RhiShader _fs;
    private readonly RhiShader _cullCs;
    private readonly RhiShader _clusterCs;
    private readonly RhiPipeline _pipeline;
    private readonly RhiPipeline _cullPipeline;
    private readonly RhiPipeline _clusterPipeline;
    private readonly RhiShader _skyVs;
    private readonly RhiShader _skyFs;
    private readonly RhiPipeline _skyPipeline;
    private readonly RhiSampler _sampler;
    private float _lastAspect;

    private RhiBuffer _instanceBuffer;
    private RhiBuffer _partBuffer;
    private RhiBuffer _materialBuffer;
    private RhiBuffer _cameraBuffer;
    private RhiBuffer _lightBuffer;
    private RhiBuffer _drawCmdBuffer;
    private RhiBuffer _drawCountBuffer;
    private RhiBuffer _clusterRecordBuffer;
    private RhiBuffer _clusterLightIndexBuffer;

    private RhiBindlessHeap _bindlessHeap;

    public unsafe PbrPass(RhiDevice device, IEntityStore world,
                              SceneGraph scene, ScenePass scenePass, string contentRoot, RhiBindlessHeap sharedHeap, Engine.Game.Renderer renderer, bool renderSky)
    {
        _device = device;
        _world = world;
        _scene = scene;
        _contentRoot = contentRoot;
        _renderer = renderer;
        _renderSky = renderSky;
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

        string clusterSrc = LoadShaderSource("shaders/cluster_lights.slang");
        _clusterCs = RhiShader.FromSource(_device, clusterSrc, "computeMain", RhiNative.ShaderStage.Compute, shaderDir);
        _clusterPipeline = RhiPipeline.CreateCompute(_device, _clusterCs);

        string skySrc = LoadShaderSource("shaders/pbr_sky.slang");
        _skyVs = RhiShader.FromSource(_device, skySrc, "vertexMain", RhiNative.ShaderStage.Vertex, shaderDir);
        _skyFs = RhiShader.FromSource(_device, skySrc, "fragmentMain", RhiNative.ShaderStage.Fragment, shaderDir);
        _skyPipeline = RhiPipeline.CreateGraphics(_device, _skyVs, _skyFs, RhiNative.TextureFormat.Bgra8Unorm, enableDepth: true);

        _sampler = RhiSampler.Create(_device);

        _instanceBuffer = RhiBuffer.Create(_device, 1024 * (ulong)sizeof(InstanceData), RhiNative.BufferUsage.Storage);
        _partBuffer = RhiBuffer.Create(_device, 4096 * (ulong)sizeof(PartData), RhiNative.BufferUsage.Storage);
        _materialBuffer = RhiBuffer.Create(_device, 1024 * (ulong)sizeof(MaterialData), RhiNative.BufferUsage.Storage);
        _cameraBuffer = RhiBuffer.Create(_device, (ulong)sizeof(CameraData), RhiNative.BufferUsage.Storage);
        _lightBuffer = RhiBuffer.Create(_device, 1024 * (ulong)sizeof(LightData), RhiNative.BufferUsage.Storage);

        _drawCmdBuffer = RhiBuffer.Create(_device, 4096 * DrawIndirectCommandSizeBytes, RhiNative.BufferUsage.Storage | RhiNative.BufferUsage.Indirect);
        _drawCountBuffer = RhiBuffer.Create(_device, DrawCountBufferSizeBytes, RhiNative.BufferUsage.Storage);
        _clusterRecordBuffer = RhiBuffer.Create(_device, 16 * (ulong)sizeof(ClusterRecord), RhiNative.BufferUsage.Storage);
        _clusterLightIndexBuffer = RhiBuffer.Create(_device, 16ul * MaxLightsPerCluster * sizeof(uint), RhiNative.BufferUsage.Storage);

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
        p.Plane0 = new Vector4(vp.M14 + vp.M11, vp.M24 + vp.M21, vp.M34 + vp.M31, vp.M44 + vp.M41);
        p.Plane1 = new Vector4(vp.M14 - vp.M11, vp.M24 - vp.M21, vp.M34 - vp.M31, vp.M44 - vp.M41);
        p.Plane2 = new Vector4(vp.M14 + vp.M12, vp.M24 + vp.M22, vp.M34 + vp.M32, vp.M44 + vp.M42);
        p.Plane3 = new Vector4(vp.M14 - vp.M12, vp.M24 - vp.M22, vp.M34 - vp.M32, vp.M44 - vp.M42);
        p.Plane4 = new Vector4(vp.M13, vp.M23, vp.M33, vp.M43);
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
            0,
            0,
            w,
            h,
            out SceneFrameData frameData,
            out ScenePushData pbrPush);

        if (frameData.Instances.Count > 0)
        {
            EnsureBuffer(ref _drawCmdBuffer, (ulong)frameData.Parts.Count * DrawIndirectCommandSizeBytes, RhiNative.BufferUsage.Storage | RhiNative.BufferUsage.Indirect);
            EnsureBuffer(ref _drawCountBuffer, DrawCountBufferSizeBytes, RhiNative.BufferUsage.Storage);
            uint clusterX = (w + ClusterTileSize - 1) / ClusterTileSize;
            uint clusterY = (h + ClusterTileSize - 1) / ClusterTileSize;
            uint clusterCount = clusterX * clusterY * ClusterDepthSlices;
            EnsureBuffer(ref _clusterRecordBuffer, (ulong)clusterCount * (ulong)sizeof(ClusterRecord), RhiNative.BufferUsage.Storage);
            EnsureBuffer(ref _clusterLightIndexBuffer, (ulong)clusterCount * MaxLightsPerCluster * sizeof(uint), RhiNative.BufferUsage.Storage);

            pbrPush.ClusterRecords = _clusterRecordBuffer.DeviceAddress;
            pbrPush.ClusterLightIndices = _clusterLightIndexBuffer.DeviceAddress;
            pbrPush.ClusterGrid = new Vector4(clusterX, clusterY, ClusterDepthSlices, clusterCount);
            pbrPush.ClusterParams = new Vector4(ClusterTileSize, MaxLightsPerCluster, 0, 0);

            uint zero = 0;
            _drawCountBuffer.Upload(new ReadOnlySpan<uint>(ref zero));

            ExtractPlanes(frameData.Camera.ViewProj, out CullPushData cullPush);
            cullPush.Instances = _instanceBuffer.DeviceAddress;
            cullPush.Parts = _partBuffer.DeviceAddress;
            cullPush.DrawCmds = _drawCmdBuffer.DeviceAddress;
            cullPush.DrawCount = _drawCountBuffer.DeviceAddress;
            cullPush.InstanceCount = (uint)frameData.Instances.Count;

            sink.BeginComputePass();
            sink.BindPipeline(_cullPipeline);
            sink.UseBuffer(_instanceBuffer, 1);
            sink.UseBuffer(_partBuffer, 1);
            sink.UseBuffer(_drawCmdBuffer, 2);
            sink.UseBuffer(_drawCountBuffer, 1);
            foreach (var mesh in frameData.UniqueMeshes)
            {
                sink.UseBuffer(mesh.VertexBuffer, 1);
                sink.UseBuffer(mesh.IndexBuffer, 1);
            }
            sink.PushConstants(0, (uint)sizeof(CullPushData), (IntPtr)(&cullPush));
            sink.Dispatch((uint)((frameData.Instances.Count + 63) / 64), 1, 1);
            sink.EndComputePass();

            sink.BeginComputePass("Cluster Light Assignment");
            sink.BindPipeline(_clusterPipeline);
            sink.UseBuffer(_cameraBuffer, 1);
            sink.UseBuffer(_lightBuffer, 1);
            sink.UseBuffer(_clusterRecordBuffer, 2);
            sink.UseBuffer(_clusterLightIndexBuffer, 2);
            sink.PushConstants(0, (uint)sizeof(ScenePushData), (IntPtr)(&pbrPush));
            sink.Dispatch((clusterCount + 63) / 64, 1, 1);
            sink.EndComputePass();

            sink.BeginRenderPass(colorTarget, RhiNative.LoadOp.Clear, RhiNative.StoreOp.Store, depthTarget);
            sink.BindPipeline(_pipeline);
            sink.SetViewport(0, 0, w, h);
            sink.UseBuffer(_instanceBuffer, 1);
            sink.UseBuffer(_partBuffer, 1);
            sink.UseBuffer(_materialBuffer, 1);
            sink.UseBuffer(_cameraBuffer, 1);
            sink.UseBuffer(_lightBuffer, 1);
            sink.UseBuffer(_clusterRecordBuffer, 1);
            sink.UseBuffer(_clusterLightIndexBuffer, 1);
            foreach (var mesh in frameData.UniqueMeshes)
            {
                sink.UseBuffer(mesh.VertexBuffer, 1);
                sink.UseBuffer(mesh.IndexBuffer, 1);
            }

            sink.PushConstants(0, (uint)sizeof(ScenePushData), (IntPtr)(&pbrPush));

            if (_renderSky)
            {
                sink.BindPipeline(_skyPipeline);
                sink.Draw(3, 1, 0, 0);
            }

            if (_bindlessHeap.IsInitialized)
            {
                sink.BindHeap(1, _bindlessHeap);
                sink.BindSampler(0, _sampler);
            }

            sink.BindPipeline(_pipeline);
            sink.DrawIndirect(_drawCmdBuffer, 0, (uint)frameData.Parts.Count, (uint)DrawIndirectCommandSizeBytes);
        }
        else
        {
            sink.BeginRenderPass(colorTarget, RhiNative.LoadOp.Clear, RhiNative.StoreOp.Store, depthTarget);
            
            sink.PushConstants(0, (uint)sizeof(ScenePushData), (IntPtr)(&pbrPush));
            
            if (_renderSky)
            {
                sink.BindPipeline(_skyPipeline);
                sink.Draw(3, 1, 0, 0);
            }
        }
        
        sink.EndPass();
    }

    private string LoadShaderSource(string relPath)
    {
        string full = Path.Combine(_contentRoot, relPath);
        if (!File.Exists(full)) throw new FileNotFoundException(full);
        return File.ReadAllText(full);
    }

    private void EnsureBuffer(ref RhiBuffer buffer, ulong requiredSize, RhiNative.BufferUsage usage)
    {
        if (requiredSize == 0) requiredSize = 16;
        if (buffer != null && buffer.Size >= requiredSize) return;

        ulong currentSize = buffer?.Size ?? 0;
        ulong newSize = currentSize == 0 ? requiredSize : currentSize;
        while (newSize < requiredSize)
            newSize *= 2;

        buffer?.Dispose();
        buffer = RhiBuffer.Create(_device, newSize, usage);
    }

    public void Dispose()
    {
        _pipeline?.Dispose();
        _cullPipeline?.Dispose();
        _clusterPipeline?.Dispose();
        _skyPipeline?.Dispose();
        _vs?.Dispose();
        _fs?.Dispose();
        _cullCs?.Dispose();
        _clusterCs?.Dispose();
        _skyVs?.Dispose();
        _skyFs?.Dispose();
        _sampler?.Dispose();

        _instanceBuffer?.Dispose();
        _partBuffer?.Dispose();
        _materialBuffer?.Dispose();
        _cameraBuffer?.Dispose();
        _lightBuffer?.Dispose();
        _drawCmdBuffer?.Dispose();
        _drawCountBuffer?.Dispose();
        _clusterRecordBuffer?.Dispose();
        _clusterLightIndexBuffer?.Dispose();
    }
}
