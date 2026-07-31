// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.RenderGraph.Shaders;
using Engine.Scene;
using Engine.CBindings;

namespace Engine.Renderer;

public class PbrPass : RenderPass
{
    private static int _nextGraphResourceId = 0x70000000;
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
    private readonly string _contentRoot;
    private readonly RasterSceneGpuCache _sceneCache;
    private readonly DirectionalShadowState? _directionalShadow;
    private readonly PunctualShadowState? _punctualShadow;
    private readonly bool _renderSky;
    private readonly IReadOnlyList<string>? _cliArgs;
    private readonly IReadOnlyList<string>? _includeDirs;
    private readonly ShaderCompileCache? _compileCache;
    private readonly IDDGIAtlasProvider? _ddgiAtlas;

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

    private RhiBuffer _instanceBuffer => _sceneCache.InstanceBuffer;
    private RhiBuffer _partBuffer => _sceneCache.PartBuffer;
    private RhiBuffer _materialBuffer => _sceneCache.MaterialBuffer;
    private RhiBuffer _cameraBuffer => _sceneCache.CameraBuffer;
    private RhiBuffer _lightBuffer => _sceneCache.LightBuffer;
    private RhiBuffer _drawCmdBuffer;
    private RhiBuffer _drawCountBuffer;
    private RhiBuffer _clusterRecordBuffer;
    private RhiBuffer _clusterLightIndexBuffer;
    private readonly ResourceHandle _drawCommandsHandle;
    private readonly ResourceHandle _drawCountHandle;
    private readonly ResourceHandle _clusterRecordsHandle;
    private readonly ResourceHandle _clusterLightIndicesHandle;
    private long _preparedFrame = -1;
    private SceneFrameData _preparedFrameData = new();
    private ScenePushData _preparedPush;

    private RhiBindlessHeap _bindlessHeap;

    internal unsafe PbrPass(
        RhiDevice device,
        ScenePass scenePass,
        string contentRoot,
        RhiBindlessHeap sharedHeap,
        RasterSceneGpuCache sceneCache,
        DirectionalShadowState? directionalShadow,
        PunctualShadowState? punctualShadow,
        bool renderSky,
        IReadOnlyList<string>? cliArgs = null,
        IReadOnlyList<string>? includeDirs = null,
        ShaderCompileCache? compileCache = null,
        IDDGIAtlasProvider? ddgiAtlas = null)
    {
        _device = device;
        _contentRoot = contentRoot;
        _sceneCache = sceneCache;
        _directionalShadow = directionalShadow;
        _punctualShadow = punctualShadow;
        _renderSky = renderSky;
        _cliArgs = cliArgs;
        _includeDirs = includeDirs;
        _compileCache = compileCache;
        _ddgiAtlas = ddgiAtlas;
        Name = string.IsNullOrWhiteSpace(scenePass.Name) ||
            scenePass.Name.Equals("PbrPass", StringComparison.OrdinalIgnoreCase)
            ? "Forward PBR"
            : $"Forward PBR · {scenePass.Name}";

        string shaderDir = Path.Combine(_contentRoot, "shaders");

        string src = LoadShaderSource("shaders/pbr.slang");
        _vs = CompileCached(src, "vertexMain", RhiNative.ShaderStage.Vertex);
        _fs = CompileCached(src, "fragmentMain", RhiNative.ShaderStage.Fragment);

        _pipeline = RhiPipeline.CreateGraphics(
            _device, _vs, _fs,
            RhiNative.TextureFormat.Bgra8Unorm,
            enableDepth: true);

        string cullSrc = LoadShaderSource("shaders/cull.slang");
        _cullCs = CompileCached(cullSrc, "computeMain", RhiNative.ShaderStage.Compute);
        _cullPipeline = RhiPipeline.CreateCompute(_device, _cullCs);

        string clusterSrc = LoadShaderSource("shaders/cluster_lights.slang");
        _clusterCs = CompileCached(clusterSrc, "computeMain", RhiNative.ShaderStage.Compute);
        _clusterPipeline = RhiPipeline.CreateCompute(_device, _clusterCs);

        string skySrc = LoadShaderSource("shaders/pbr_sky.slang");
        _skyVs = CompileCached(skySrc, "vertexMain", RhiNative.ShaderStage.Vertex);
        _skyFs = CompileCached(skySrc, "fragmentMain", RhiNative.ShaderStage.Fragment);
        _skyPipeline = RhiPipeline.CreateGraphics(_device, _skyVs, _skyFs, RhiNative.TextureFormat.Bgra8Unorm, enableDepth: true);

        _sampler = RhiSampler.Create(_device);

        _drawCmdBuffer = RhiBuffer.Create(_device, 4096 * DrawIndirectCommandSizeBytes, RhiNative.BufferUsage.Storage | RhiNative.BufferUsage.Indirect);
        _drawCountBuffer = RhiBuffer.Create(_device, DrawCountBufferSizeBytes, RhiNative.BufferUsage.Storage);
        _clusterRecordBuffer = RhiBuffer.Create(_device, 16 * (ulong)sizeof(ClusterRecord), RhiNative.BufferUsage.Storage);
        _clusterLightIndexBuffer = RhiBuffer.Create(_device, 16ul * MaxLightsPerCluster * sizeof(uint), RhiNative.BufferUsage.Storage);

        _bindlessHeap = sharedHeap;
        _drawCommandsHandle = NextGraphResourceHandle();
        _drawCountHandle = NextGraphResourceHandle();
        _clusterRecordsHandle = NextGraphResourceHandle();
        _clusterLightIndicesHandle = NextGraphResourceHandle();
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Write(RenderGraphResources.BackBufferHandle, ResourceState.RenderTarget);
        builder.Write(RenderGraphResources.DepthBufferHandle, ResourceState.DepthStencil);
        if (_directionalShadow != null)
        {
            for (int cascadeIndex = 0;
                 cascadeIndex < DirectionalShadowState.CascadeCount;
                 ++cascadeIndex)
            {
                builder.Read(
                    Engine.Renderer.Renderer.GetDirectionalShadowMapHandle(
                        cascadeIndex),
                    ResourceState.ShaderRead);
            }
        }
        if (_punctualShadow != null)
        {
            for (int pageIndex = 4; pageIndex < 24; ++pageIndex)
            {
                builder.Read(
                    RenderGraphResources.GetShadowPageHandle(pageIndex),
                    ResourceState.ShaderRead);
            }
        }
        builder.Read(_drawCommandsHandle, ResourceState.ShaderRead);
        builder.Read(_drawCountHandle, ResourceState.ShaderRead);
        builder.Read(_clusterRecordsHandle, ResourceState.ShaderRead);
        builder.Read(_clusterLightIndicesHandle, ResourceState.ShaderRead);
    }

    internal RenderPass CreateComputePass() => new RasterComputePass(this);

    internal void SetupCompute(RenderGraphBuilder builder)
    {
        builder.Write(_drawCommandsHandle, ResourceState.UnorderedAccess);
        builder.Write(_drawCountHandle, ResourceState.UnorderedAccess);
        builder.Write(_clusterRecordsHandle, ResourceState.UnorderedAccess);
        builder.Write(_clusterLightIndicesHandle, ResourceState.UnorderedAccess);
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

    internal unsafe void ExecuteCompute(
        ICommandSink sink,
        RenderGraphContext context)
    {
        uint w = context.Width > 0 ? context.Width : 1280;
        uint h = context.Height > 0 ? context.Height : 720;
        _lastAspect = (float)w / h;

        _sceneCache.Prepare(context.FrameNumber, _lastAspect, w, h);
        SceneFrameData frameData = _sceneCache.FrameData;
        ScenePushData pbrPush = _sceneCache.PushData;
        PopulateShadowData(ref pbrPush);
        _preparedFrame = context.FrameNumber;
        _preparedFrameData = frameData;
        _preparedPush = pbrPush;

        if (frameData.Instances.Count == 0)
            return;

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
        _preparedPush = pbrPush;

        uint zero = 0;
        _drawCountBuffer.Upload(new ReadOnlySpan<uint>(ref zero));

        ExtractPlanes(frameData.Camera.ViewProj, out CullPushData cullPush);
        cullPush.Instances = _instanceBuffer.DeviceAddress;
        cullPush.Parts = _partBuffer.DeviceAddress;
        cullPush.DrawCmds = _drawCmdBuffer.DeviceAddress;
        cullPush.DrawCount = _drawCountBuffer.DeviceAddress;
        cullPush.InstanceCount = (uint)frameData.Instances.Count;

        sink.BeginComputePass("Raster Visibility");
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
    }

    public override unsafe void Execute(ICommandSink sink, RenderGraphContext context)
    {
        if (!context.TryGetTexture(RenderGraphResources.BackBufferHandle, out RhiTexture colorTarget))
            return;
        context.TryGetTexture(RenderGraphResources.DepthBufferHandle, out RhiTexture depthTarget);

        uint w = context.Width > 0 ? context.Width : 1280;
        uint h = context.Height > 0 ? context.Height : 720;
        if (_preparedFrame != context.FrameNumber)
            ExecuteCompute(sink, context);

        SceneFrameData frameData = _preparedFrameData;
        ScenePushData pbrPush = _preparedPush;
        PopulateShadowData(ref pbrPush);

        if (frameData.Instances.Count > 0)
        {
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
            if (_punctualShadow != null)
                sink.UseBuffer(_punctualShadow.FaceBuffer, 1);
            if (_ddgiAtlas != null &&
                _ddgiAtlas.TryGetSparseBuffers(
                    out RhiBuffer probePositions,
                    out RhiBuffer gridToProbeIndex,
                    out RhiBuffer probeCounter) &&
                _ddgiAtlas.IsSparseLayoutReady)
            {
                sink.UseBuffer(probePositions, 5);
                sink.UseBuffer(gridToProbeIndex, 6);
            }
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

    private void PopulateShadowData(ref ScenePushData pbrPush)
    {
        pbrPush.DirectionalShadowViewProj =
            _directionalShadow?.ViewProjections[0] ?? Matrix4x4.Identity;
        pbrPush.DirectionalShadowParams =
            _directionalShadow?.Parameters ?? Vector4.Zero;
        if (_directionalShadow != null)
        {
            pbrPush.DirectionalShadowViewProj1 =
                _directionalShadow.ViewProjections[1];
            pbrPush.DirectionalShadowViewProj2 =
                _directionalShadow.ViewProjections[2];
            pbrPush.DirectionalShadowViewProj3 =
                _directionalShadow.ViewProjections[3];
            pbrPush.DirectionalShadowSplits = _directionalShadow.Splits;
            pbrPush.DirectionalShadowTextureIndices = new Vector4(
                _directionalShadow.TextureSlots[0],
                _directionalShadow.TextureSlots[1],
                _directionalShadow.TextureSlots[2],
                _directionalShadow.TextureSlots[3]);
        }
        if (_punctualShadow != null)
        {
            pbrPush.PunctualShadowFaces =
                _punctualShadow.FaceBuffer.DeviceAddress;
            pbrPush.PunctualShadowFaceCount = 1024 * 6;
        }

        // DDGI consumer data. Zeroed when the plugin is not loaded
        // so the shader-side `DDGI_AVAILABLE` macro can fall back to
        // no-atlas sampling without host coupling. The sparse
        // positions / gridToProbe / counter SSBOs are bound at
        // fixed register slots (t5/t6/u0) by the per-frame
        // UseBuffer block inside Execute() when the plugin reports
        // its sparse layout ready; the .w component of
        // DDGIAtlasParams encodes that ready flag as 0.0 (pending)
        // or 1.0 (consumers should read sparse SSBOs).
        if (_ddgiAtlas != null &&
            _ddgiAtlas.TryGetProbeVolume(
                out Vector3 origin,
                out Vector3 extent,
                out Engine.RenderGraph.Vector3I baseGrid))
        {
            var (irradSlot, visSlot) =
                _ddgiAtlas.GetAtlasBindlessSlots();
            int packedGrid =
                baseGrid.X * baseGrid.Y * baseGrid.Z;
            pbrPush.DDGIAtlasParams = new Vector4(
                irradSlot,
                visSlot,
                (float)packedGrid,
                _ddgiAtlas.IsSparseLayoutReady ? 1f : 0f);
            pbrPush.DDGIOriginAndCountZ = new Vector4(
                origin.X,
                origin.Y,
                origin.Z,
                baseGrid.Z);
            pbrPush.DDGIExtentAndFlags = new Vector4(
                extent.X,
                extent.Y,
                _ddgiAtlas.RaysPerProbe,
                _ddgiAtlas.MaxProbesPerFrame);
        }
        else
        {
            pbrPush.DDGIAtlasParams = Vector4.Zero;
            pbrPush.DDGIOriginAndCountZ = Vector4.Zero;
            pbrPush.DDGIExtentAndFlags = Vector4.Zero;
        }
    }

    private static ResourceHandle NextGraphResourceHandle()
        => new(unchecked((uint)Interlocked.Increment(ref _nextGraphResourceId)));

    private string LoadShaderSource(string relPath)
    {
        string full = Path.Combine(_contentRoot, relPath);
        if (!File.Exists(full)) throw new FileNotFoundException(full);
        return File.ReadAllText(full);
    }

    private RhiShader CompileCached(
        string source, string entry, RhiNative.ShaderStage stage)
    {
        string shaderDir = Path.Combine(_contentRoot, "shaders");
        IReadOnlyList<string>? dirs = _includeDirs ?? new[] { shaderDir };
        if (_compileCache == null)
            return RhiShader.FromSource(_device, source, entry, stage, dirs, _cliArgs);
        return (RhiShader)_compileCache.GetOrCompileHash(
            source, entry, stage, dirs, _cliArgs,
            () => RhiShader.FromSource(_device, source, entry, stage, dirs, _cliArgs));
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
        _sampler?.Dispose();
        _drawCmdBuffer?.Dispose();
        _drawCountBuffer?.Dispose();
        _clusterRecordBuffer?.Dispose();
        _clusterLightIndexBuffer?.Dispose();
    }
}

internal sealed class RasterComputePass : RenderPass
{
    private readonly PbrPass _owner;

    public RasterComputePass(PbrPass owner)
    {
        _owner = owner;
        Name = "Raster Culling + Light Clusters";
        Queue = RhiNative.QueueType.Compute;
    }

    public override void Setup(RenderGraphBuilder builder)
        => _owner.SetupCompute(builder);

    public override void Execute(ICommandSink sink, RenderGraphContext context)
        => _owner.ExecuteCompute(sink, context);
}
