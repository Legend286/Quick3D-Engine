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

/// <summary>
/// Phase-2 CPU-only probe marker overlay pass. Reads the plugin's
/// registered <see cref="DDGIProbeVolume"/> + queries the host's
/// active-camera-pose service (<see cref="IEnginePluginHost.TryGetActiveCameraData"/>)
/// per frame so the pass needs ZERO Engine.Renderer coupling.
/// Plugin-flagged via the editor's <see cref="DDGIVolumeRegistry.ShowProbes"/>
/// static gate; the canonical clustered plan consults that gate when
/// injecting the pass.
/// </summary>
public sealed class DDGIDebugPass : RenderPass, IDisposable
{
    private readonly RhiDevice _device;
    private readonly DDGIProbeVolume _volume;
    private readonly string _contentRoot;
    private readonly IReadOnlyList<string>? _cliArgs;
    private readonly IReadOnlyList<string>? _includeDirs;
    private readonly ShaderCompileCache? _compileCache;
    private readonly IEnginePluginHost _host;

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
        DDGIProbeVolume volume,
        IEnginePluginHost host,
        string contentRoot,
        IReadOnlyList<string>? cliArgs,
        IReadOnlyList<string>? includeDirs,
        ShaderCompileCache? compileCache)
    {
        _device = device;
        _volume = volume;
        _host = host;
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
            RenderGraphResources.BackBufferHandle,
            ResourceState.RenderTarget);
    }

    public override unsafe void Execute(
        ICommandSink sink, RenderGraphContext context)
    {
        // Defensive: the editor can flip the DDGI Probes overlay on
        // before the GPU placement kernel has populated the sparse
        // layout (e.g. first frame after plugin enable). ProbeCount
        // is the safest non-throwing initialization gate; rendering
        // nothing is preferable to NRE-ing the render thread.
        if (!_volume.IsInitialized || _volume.ProbeCount <= 0)
            return;
        if (_probePositionsBuffer == null)
            return;

        if (!context.TryGetTexture(
            RenderGraphResources.BackBufferHandle,
            out RhiTexture colorTarget))
            return;

        if (_uploadedFrame != context.FrameNumber)
        {
            int probeCount = _volume.ProbeCount;
            var positions = new Vector3[probeCount];
            for (int i = 0; i < positions.Length; ++i)
            {
                try { positions[i] = _volume.PositionAt(i); }
                catch { positions[i] = Vector3.Zero; }
            }
            _probePositionsBuffer.Upload(new ReadOnlySpan<Vector3>(positions));
            _uploadedFrame = context.FrameNumber;
        }

        if (!_host.TryGetActiveCameraData(
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
