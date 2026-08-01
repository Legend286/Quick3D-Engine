// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.CBindings;
using Engine.Plugins;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.RenderGraph.Shaders;

namespace Engine.DDGI;

/// <summary>Draws the current geometry-driven DDGI request stream.</summary>
public sealed class DDGIDebugPass : RenderPass, IDisposable
{
    private readonly RhiDevice _device;
    private readonly string _contentRoot;
    private readonly IReadOnlyList<string>? _cliArgs;
    private readonly IReadOnlyList<string>? _includeDirs;
    private readonly ShaderCompileCache? _compileCache;
    private readonly IActiveCameraDataProvider _cameraProvider;
    private readonly DDGIAtlasResources _atlas;

    private readonly RhiShader _vs;
    private readonly RhiShader _fs;
    private readonly RhiPipeline _pipeline;
    private bool _loggedNoCamera;

    [StructLayout(LayoutKind.Sequential)]
    private struct DebugPush
    {
        public Matrix4x4 ViewProj;
        public uint RequestCount;
        public float HalfSize;
        public uint ClipmapLevelCount;
        public uint ShowStatusColors;
        public uint IrradianceBindlessIndex;
        public uint Padding0;
        public ulong ProbeRequests;
        public ulong ProbePositions;
        public ulong ProbeStates;
        public ulong GridToProbeIndex;
    }

    private const long Vector3SizeBytes = 12;

    public DDGIDebugPass(
        RhiDevice device,
        DDGIAtlasResources atlas,
        IActiveCameraDataProvider cameraProvider,
        string contentRoot,
        IReadOnlyList<string>? cliArgs,
        IReadOnlyList<string>? includeDirs,
        ShaderCompileCache? compileCache)
    {
        _device = device;
        _atlas = atlas;
        _cameraProvider = cameraProvider;
        _contentRoot = contentRoot;
        _cliArgs = cliArgs;
        _includeDirs = includeDirs;
        _compileCache = compileCache;
        Name = "DDGI Probes (Debug)";

        string src = LoadShaderSource();
        _vs = CompileCached(src, "vsMain", RhiNative.ShaderStage.Vertex);
        _fs = CompileCached(src, "fsMain", RhiNative.ShaderStage.Fragment);
        _pipeline = RhiPipeline.CreateGraphics(
            _device, _vs, _fs,
            RhiNative.TextureFormat.Bgra8Unorm,
            enableDepth: false,
            enableDepthWrite: false);
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.ImportBuffer(_atlas.ResourceHandles.ProbePositions);
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeStates);
        builder.ImportBuffer(_atlas.ResourceHandles.GridToProbeIndex);
        builder.ImportTexture(_atlas.ResourceHandles.Irradiance);
        builder.Read(
            _atlas.ResourceHandles.ProbePositions,
            ResourceState.ShaderRead);
        builder.Read(
            _atlas.ResourceHandles.ProbeStates,
            ResourceState.ShaderRead);
        builder.Read(
            _atlas.ResourceHandles.GridToProbeIndex,
            ResourceState.ShaderRead);
        builder.Read(
            _atlas.ResourceHandles.Irradiance,
            ResourceState.ShaderRead);
        builder.Write(
            RenderGraphResources.BackBufferHandle,
            ResourceState.RenderTarget);
    }

    public override unsafe void Execute(
        ICommandSink sink, RenderGraphContext context)
    {
        if (!DDGIVolumeRegistry.ShowProbes)
            return;

        if (!context.TryGetTexture(
            RenderGraphResources.BackBufferHandle,
            out RhiTexture colorTarget))
            return;

        if (!_cameraProvider.TryGetViewportCameraData(
                context.Width, context.Height,
                out _,
                out Matrix4x4 viewProj,
                out _))
        {
            if (!_loggedNoCamera)
            {
                _loggedNoCamera = true;
                Log.Info(
                    "[DDGI] Probe viz skipped: host returned no active camera data.",
                    "DDGI");
            }
            return;
        }
        _loggedNoCamera = false;

        DebugPush push = new()
        {
            ViewProj = viewProj,
            RequestCount = (uint)_atlas.RequestCount,
            HalfSize = 0.30f,
            ClipmapLevelCount = (uint)_atlas.ClipmapLevelCount,
            ShowStatusColors =
                DDGIVolumeRegistry.ShowProbeStatusColors ? 1u : 0u,
            IrradianceBindlessIndex = _atlas.IrradianceBindlessIndex,
            ProbeRequests = _atlas.ProbeRequests.DeviceAddress,
            ProbePositions = _atlas.ProbePositions.DeviceAddress,
            ProbeStates = _atlas.ProbeStates.DeviceAddress,
            GridToProbeIndex = _atlas.GridToProbeIndex.DeviceAddress,
        };

        sink.BeginRenderPass(
            colorTarget,
            RhiNative.LoadOp.Load,
            RhiNative.StoreOp.Store);
        sink.BindPipeline(_pipeline);
        sink.SetViewport(
            0.0f, 0.0f,
            (float)context.Width,
            (float)context.Height);
        sink.UseBuffer(_atlas.ProbeRequests, 1);
        sink.UseBuffer(_atlas.ProbePositions, 1);
        sink.UseBuffer(_atlas.ProbeStates, 1);
        sink.UseBuffer(_atlas.GridToProbeIndex, 1);
        if (_atlas.SharedHeap.IsInitialized)
            sink.BindHeap(1, _atlas.SharedHeap);
        sink.PushConstants(
            0,
            (uint)sizeof(DebugPush),
            (IntPtr)(&push));
        sink.Draw((uint)_atlas.RequestCount * 24u, 1, 0, 0);
        sink.EndPass();
    }

    private string LoadShaderSource()
    {
        foreach (string dir in _includeDirs ?? Array.Empty<string>())
        {
            string candidate = Path.Combine(dir, "ddgi_debug.slang");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
        }
        string fallback = Path.Combine(_contentRoot, "shaders", "ddgi_debug.slang");
        if (File.Exists(fallback))
            return File.ReadAllText(fallback);
        throw new FileNotFoundException(
            $"ddgi_debug.slang not found in include dirs or {fallback}.");
    }

    private RhiShader CompileCached(
        string source, string entry, RhiNative.ShaderStage stage)
    {
        if (_compileCache == null)
            return RhiShader.FromSource(_device, source, entry, stage, _includeDirs, _cliArgs);
        return (RhiShader)_compileCache.GetOrCompileHash(
            source, entry, stage, _includeDirs, _cliArgs,
            () => RhiShader.FromSource(_device, source, entry, stage, _includeDirs, _cliArgs));
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _fs.Dispose();
        _vs.Dispose();
    }
}
