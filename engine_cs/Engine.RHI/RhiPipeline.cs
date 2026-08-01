// SPDX-License-Identifier: MIT
// Pipeline wrapper.

using System;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiPipeline : IDisposable
{
    public IntPtr Handle { get; private set; }

    internal RhiPipeline(IntPtr handle) { Handle = handle; }

    public static RhiPipeline CreateGraphics(RhiDevice device, RhiShader vertexShader, RhiShader fragmentShader,
        RhiNative.TextureFormat colorFormat, bool enableDepth = true, bool enableDepthWrite = true, bool enableBlend = false, RhiNative.PrimitiveTopology topology = RhiNative.PrimitiveTopology.TriangleList, RhiNative.CompareOp depthCompare = RhiNative.CompareOp.LessEqual)
    {
        // MARK: Defensive guards. Without these a GC finaliser on the
        // shader wrappers can run mid-P/Invoke; the Dispose finalizer
        // path zeroes the Handle field (see RhiShader.Dispose) which
        // then marshals as IntPtr.Zero down to the Metal backend, where
        // the cast to RhiShaderImpl* and subsequent cs->fn dereference
        // trips EXC_BAD_ACCESS at the ISA pointer offset (0x8).
        // Validating here gives a clear ObjectDisposedException at the
        // C# call site, and GC.KeepAlive pins both shader references
        // alive across the native call so a concurrent finaliser on a
        // render-thread worker cannot stale the handle mid-call.
        if (vertexShader == null || vertexShader.Handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(vertexShader),
                "Vertex shader was disposed before the graphics pipeline could be created.");
        if (fragmentShader == null || fragmentShader.Handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(fragmentShader),
                "Fragment shader was disposed before the graphics pipeline could be created.");
        var desc = new RhiNative.GraphicsPipelineDesc
        {
            Abi = 5,
            VertexShader = vertexShader.Handle,
            FragmentShader = fragmentShader.Handle,
            ColorFormat = colorFormat,
            EnableDepth = enableDepth ? 1 : 0,
            EnableDepthWrite = enableDepthWrite ? 1 : 0,
            EnableBlend = enableBlend ? 1 : 0,
            SampleCount = 1,
            PrimitiveTopology = (uint)topology,
            DepthCompare = depthCompare,
            ColorAttachmentCount =
                colorFormat == RhiNative.TextureFormat.Undefined ? 0u : 1u,
        };
        int res = RhiNative.RhiCreateGraphicsPipeline(device.Handle, in desc, out IntPtr handle);
        GC.KeepAlive(vertexShader);
        GC.KeepAlive(fragmentShader);
        if (res != 0 || handle == IntPtr.Zero)
            throw new Exception("Failed to create graphics pipeline.");
        return new RhiPipeline(handle);
    }

    /// <summary>Creates a graphics pipeline with two color attachments.</summary>
    public static RhiPipeline CreateGraphicsMrt(
        RhiDevice device,
        RhiShader vertexShader,
        RhiShader fragmentShader,
        RhiNative.TextureFormat colorFormat0,
        RhiNative.TextureFormat colorFormat1,
        bool enableDepth = true,
        bool enableDepthWrite = true,
        RhiNative.CompareOp depthCompare = RhiNative.CompareOp.LessEqual)
    {
        if (vertexShader == null || vertexShader.Handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(vertexShader));
        if (fragmentShader == null || fragmentShader.Handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(fragmentShader));
        if (colorFormat0 == RhiNative.TextureFormat.Undefined ||
            colorFormat1 == RhiNative.TextureFormat.Undefined)
        {
            throw new ArgumentException(
                "MRT color attachment formats must be defined.");
        }

        var desc = new RhiNative.GraphicsPipelineDesc
        {
            Abi = 5,
            VertexShader = vertexShader.Handle,
            FragmentShader = fragmentShader.Handle,
            ColorFormat = colorFormat0,
            EnableDepth = enableDepth ? 1 : 0,
            EnableDepthWrite = enableDepthWrite ? 1 : 0,
            EnableBlend = 0,
            SampleCount = 1,
            PrimitiveTopology =
                (uint)RhiNative.PrimitiveTopology.TriangleList,
            DepthCompare = depthCompare,
            ColorFormat1 = colorFormat1,
            ColorAttachmentCount = 2,
        };
        int result = RhiNative.RhiCreateGraphicsPipeline(
            device.Handle,
            in desc,
            out IntPtr handle);
        GC.KeepAlive(vertexShader);
        GC.KeepAlive(fragmentShader);
        if (result != 0 || handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"rhi_create_graphics_pipeline rc={result}");
        return new RhiPipeline(handle);
    }

    /// <summary>
    /// Creates a graphics pipeline with a depth attachment and no color attachment.
    /// </summary>
    public static RhiPipeline CreateDepthOnly(RhiDevice device, RhiShader vertexShader, RhiShader fragmentShader)
        => CreateGraphics(device, vertexShader, fragmentShader, RhiNative.TextureFormat.Undefined);

    public static RhiPipeline CreateDepthClear(
        RhiDevice device,
        RhiShader vertexShader,
        RhiShader fragmentShader)
        => CreateGraphics(
            device,
            vertexShader,
            fragmentShader,
            RhiNative.TextureFormat.Undefined,
            depthCompare: RhiNative.CompareOp.Always);

    public static RhiPipeline CreateCompute(RhiDevice device, RhiShader computeShader)
    {
        // MARK: Defensive guards. See the matching comment in
        // CreateGraphics for the fault chain; the previous form
        // silently forwarded a Handle==IntPtr.Zero (from a disposed
        // or GCed shader) to compute-pipeline creation, crashing the
        // Metal backend inside cs->fn at offset 0x8.
        if (computeShader == null || computeShader.Handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(computeShader),
                "Compute shader was disposed before the compute pipeline could be created.");
        var desc = new RhiNative.ComputePipelineDesc
        {
            Abi = 2,
            ComputeShader = computeShader.Handle
        };
        int rc = RhiNative.RhiCreateComputePipeline(device.Handle, in desc, out IntPtr p);
        GC.KeepAlive(computeShader);
        if (rc != 0) throw new InvalidOperationException($"rhi_create_compute_pipeline rc={rc}");
        return new RhiPipeline(p);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        // Zero the Handle field BEFORE invoking the native destroy so a
        // failed/partial C-side free doesn't get repeated by the finalizer.
        var h = Handle;
        Handle = IntPtr.Zero;
        RhiNative.RhiDestroyPipeline(h);
        GC.SuppressFinalize(this);
    }

    private string? _debugName;
    private string _debugCategory = "Pipeline";

    /// <summary>Setter pair with <see cref="RhiBuffer.SetDebugName"/>.
    /// Records the label in managed-side storage keyed by this
    /// instance's <see cref="Handle"/> so renderer diagnostics can
    /// surface pipeline provenance (probe updates, shadow cascades,
    /// blit passes) without the C RHI round-trip the buffer/texture
    /// paths perform via <c>GpuResourceRegistry</c>.</summary>
    public void SetDebugName(string name, string category = "Pipeline")
    {
        _debugName = name ?? throw new ArgumentNullException(nameof(name));
        _debugCategory = category ?? "Pipeline";
    }

    /// <summary>Gets the most-recent label assigned via
    /// <see cref="SetDebugName"/>, or <c>null</c> if no label was set.</summary>
    public string? DebugName => _debugName;

    /// <summary>Gets the diagnostic category assigned alongside the label.</summary>
    public string DebugCategory => _debugCategory;

    /// <summary>Safety net: see <see cref="RhiBuffer"/>.</summary>
    ~RhiPipeline() => Dispose();
}
