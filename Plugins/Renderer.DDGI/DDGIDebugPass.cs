// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;

namespace Engine.DDGI;

public sealed class DDGIDebugPass : RenderPass, IDisposable
{
    private readonly RhiDevice _device;
    private readonly Engine.Renderer.DDGI.DDGIProbeVolume _volume;
    private readonly string _contentRoot;
    private readonly IReadOnlyList<string>? _cliArgs;
    private readonly IReadOnlyList<string>? _includeDirs;
    private readonly Engine.Renderer.ShaderCompileCache? _compileCache;
    private readonly Engine.Renderer.Renderer _renderer;

    private readonly RhiShader _vs;
    private readonly RhiShader _fs;
    private readonly RhiPipeline _pipeline;
    private RhiBuffer _probePositionsBuffer;
    private long _uploadedFrame = -1;
    private bool _loggedNoCamera;

    // Slang cbuffer round-up: Metal's `setVertexBytes` auto-pads the
    // upload to a 16-byte multiple and `ConstantBuffer<DDGIDebugPush>`
    // reserves 80 bytes. The C# struct is 76 bytes; the trailing `Pad`
    // zero-initialises bytes 76-79 so the cbuffer's reserved-but-
    // unwritten tail never carries state from a previous push.
    [StructLayout(LayoutKind.Sequential)]
    private struct DebugPush
    {
        public Matrix4x4 ViewProj;
        public uint ProbeCount;
        public float HalfSize;
        public float ExtentY;
        public uint Pad;
    }

    private const long Vector3SizeBytes = 12;

    public DDGIDebugPass(
        RhiDevice device,
        Engine.Renderer.DDGI.DDGIProbeVolume volume,
        Engine.Renderer.Renderer renderer,
        string contentRoot,
        IReadOnlyList<string>? cliArgs,
        IReadOnlyList<string>? includeDirs,
        Engine.Renderer.ShaderCompileCache? compileCache)
    {
        _device = device;
        _volume = volume;
        _renderer = renderer;
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

        _probePositionsBuffer = RhiBuffer.Create(
            _device,
            (ulong)volume.ProbeCount * (ulong)Vector3SizeBytes,
            RhiNative.BufferUsage.Storage);
        _probePositionsBuffer.SetDebugName("DDGI Probe Positions", "DDGI");
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Write(
            Engine.Renderer.Renderer.BackBufferHandle,
            ResourceState.RenderTarget);
    }

    public override unsafe void Execute(
        ICommandSink sink, RenderGraphContext context)
    {
        if (!context.TryGetTexture(
            Engine.Renderer.Renderer.BackBufferHandle,
            out RhiTexture colorTarget))
            return;

        if (_uploadedFrame != context.FrameNumber)
        {
            var positions = new Vector3[_volume.ProbeCount];
            for (int i = 0; i < positions.Length; ++i)
                positions[i] = _volume.PositionAt(i);
            _probePositionsBuffer.Upload(new ReadOnlySpan<Vector3>(positions));
            _uploadedFrame = context.FrameNumber;
        }

        if (!_renderer.TryGetActiveCameraData(
                context.Width, context.Height,
                out _,
                out _,
                out Engine.Renderer.CameraData cameraData))
        {
            if (!_loggedNoCamera)
            {
                _loggedNoCamera = true;
                Log.Info(
                    "[DDGI] Probe viz skipped: no active camera entity on Renderer.",
                    "DDGI");
            }
            return;
        }
        _loggedNoCamera = false;

        DebugPush push = new()
        {
            ViewProj = cameraData.ViewProj,
            ProbeCount = (uint)_volume.ProbeCount,
            HalfSize = 0.30f,
            ExtentY = _volume.Extent.Y,
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
        sink.UseBuffer(_probePositionsBuffer, 1);
        sink.PushConstants(
            0,
            (uint)sizeof(DebugPush),
            (IntPtr)(&push));
        sink.Draw((uint)(_volume.ProbeCount * 4), 1);
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
        _pipeline?.Dispose();
        _probePositionsBuffer?.Dispose();
    }
}
