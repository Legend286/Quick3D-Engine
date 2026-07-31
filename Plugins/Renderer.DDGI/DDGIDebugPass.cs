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
    private readonly DDGIAtlasResources _atlas;

    private readonly RhiShader _vs;
    private readonly RhiShader _fs;
    private readonly RhiPipeline _pipeline;
    private long _uploadedFrame = -1;
    private bool _loggedNoCamera;

    // MARK: Per-gate early-return diagnostics. Each gate tracks
    // whether it fired on the previous tick; on a transition (gate
    // flipped from firing→passing or passing→firing) we log once
    // so the user can pin exactly which condition is killing
    // visibility. State stabilises after the first transition so
    // log spam is bounded to O(gates) lines under steady state.
    private const int GateSampleEvery = 60;
    private long _executeTick;
    private bool _prevShowProbesGate;
    private bool _prevVolumeInitGate;
    private bool _prevSparseReadyGate;
    private bool _prevColorTargetGate;

    // Slang cbuffer round-up: Metal's `setVertexBytes` auto-pads the
    // upload to a 16-byte multiple and `ConstantBuffer<DDGIDebugPush>`
    // reserves 88 bytes (matches the shader's DDGIDebugPush struct:
    // 64-byte matrix + 6 trailing scalars + 4-byte pad). OriginY +
    // HalfHeight feed the spatial fallback gradient so probes are
    // visible across any volume the plugin constructs.
    [StructLayout(LayoutKind.Sequential)]
    private struct DebugPush
    {
        public Matrix4x4 ViewProj;
        public uint ProbeCount;
        public float HalfSize;
        public float OriginY;
        public float HalfHeight;
        public uint IrradianceBindlessIndex;
        public uint Pad;
    }

    private const long Vector3SizeBytes = 12;

    public DDGIDebugPass(
        RhiDevice device,
        DDGIProbeVolume volume,
        DDGIAtlasResources atlas,
        IEnginePluginHost host,
        string contentRoot,
        IReadOnlyList<string>? cliArgs,
        IReadOnlyList<string>? includeDirs,
        ShaderCompileCache? compileCache)
    {
        _device = device;
        _volume = volume;
        _atlas = atlas;
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
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.ImportBuffer(_atlas.ResourceHandles.ProbePositions);
        builder.ImportBuffer(_atlas.ResourceHandles.ProbeDrawArgs);
        builder.Read(
            _atlas.ResourceHandles.ProbePositions,
            ResourceState.ShaderRead);
        builder.Read(
            _atlas.ResourceHandles.ProbeDrawArgs,
            ResourceState.ShaderRead);
        builder.Write(
            RenderGraphResources.BackBufferHandle,
            ResourceState.RenderTarget);
    }

    public override unsafe void Execute(
        ICommandSink sink, RenderGraphContext context)
    {
        ++_executeTick;
        bool sampledThisTick = _executeTick <= 30 ||
            _executeTick % GateSampleEvery == 0;

        bool showProbesGate = !DDGIVolumeRegistry.ShowProbes;
        if (showProbesGate)
        {
            LogGateTransition(nameof(DDGIDebugPass),
                "ShowProbes",
                ref _prevShowProbesGate,
                fired: true,
                sampledThisTick);
            return;
        }
        _prevShowProbesGate = LogGateTransition(nameof(DDGIDebugPass),
            "ShowProbes",
            ref _prevShowProbesGate,
            fired: false,
            sampledThisTick);

        if (!_volume.IsInitialized)
        {
            _prevVolumeInitGate = LogGateTransition(nameof(DDGIDebugPass),
                "VolumeInitialized",
                ref _prevVolumeInitGate,
                fired: true,
                sampledThisTick);
            return;
        }
        _prevVolumeInitGate = LogGateTransition(nameof(DDGIDebugPass),
            "VolumeInitialized",
            ref _prevVolumeInitGate,
            fired: false,
            sampledThisTick);

        if (!_atlas.SparseLayoutReady)
        {
            _prevSparseReadyGate = LogGateTransition(nameof(DDGIDebugPass),
                "SparseLayoutReady",
                ref _prevSparseReadyGate,
                fired: true,
                sampledThisTick);
            return;
        }
        _prevSparseReadyGate = LogGateTransition(nameof(DDGIDebugPass),
            "SparseLayoutReady",
            ref _prevSparseReadyGate,
            fired: false,
            sampledThisTick);

        if (!context.TryGetTexture(
            RenderGraphResources.BackBufferHandle,
            out RhiTexture colorTarget))
        {
            _prevColorTargetGate = LogGateTransition(nameof(DDGIDebugPass),
                "ColorTarget",
                ref _prevColorTargetGate,
                fired: true,
                sampledThisTick);
            return;
        }
        _prevColorTargetGate = LogGateTransition(nameof(DDGIDebugPass),
            "ColorTarget",
            ref _prevColorTargetGate,
            fired: false,
            sampledThisTick);

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
            ProbeCount = (uint)_atlas.MaxProbesTotalBudget,
            HalfSize = 0.30f,
            OriginY = _atlas.Origin.Y,
            HalfHeight = _atlas.Extent.Y,
            IrradianceBindlessIndex = _atlas.IrradianceBindlessIndex,
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
        sink.BindVertexBuffer(5, _atlas.ProbePositions);
        sink.UseBuffer(_atlas.ProbePositions, 1);
        sink.UseBuffer(_atlas.ProbeDrawArgs, 1);
        if (_atlas.SharedHeap != null && _atlas.SharedHeap.IsInitialized)
            sink.BindHeap(1, _atlas.SharedHeap);
        sink.PushConstants(
            0,
            (uint)sizeof(DebugPush),
            (IntPtr)(&push));
        sink.DrawIndirect(_atlas.ProbeDrawArgs, 0, 1, 16);
        sink.EndPass();
    }

    /// <summary>Logs a per-gate state-transition event for the
    /// disappearance audit. Fires when a gate's "fired on previous
    /// tick" boolean disagrees with the current tick's value — log
    /// the transition once at the moment of change. Steady-state
    /// firing is sampled at <see cref="GateSampleEvery"/> intervals
    /// so a perpetually-failing gate stays observable without
    /// spamming the log every frame. Returns the just-fired value
    /// so callers can assign it into their `_prevXxxGate` field for
    /// the next tick's comparison.</summary>
    /// <remarks>Wording is deliberately neutral ("no longer
    /// blocking this gate" rather than "rendering resumes") because
    /// a single gate clearing does not imply all gates cleared —
    /// other gates higher in the call chain may still be latched.
    /// See docs/renderer/ddgi.md#disappearance-audit.</remarks>
    private static bool LogGateTransition(
        string passName,
        string gateName,
        ref bool prevFired,
        bool fired,
        bool sampledThisTick)
    {
        if (fired == prevFired)
        {
            if (fired && sampledThisTick)
            {
                Log.Info(
                    $"[DDGI] {passName} {gateName} gate still firing " +
                    $"(steady-state sample)",
                    "DDGI");
            }
            return fired;
        }
        prevFired = fired;
        Log.Info(
            $"[DDGI] {passName} {gateName} gate " +
            (fired ? "LATCHED (blocking render)"
                   : "CLEARED (this gate no longer blocking)"),
            "DDGI");
        return fired;
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
    }
}
