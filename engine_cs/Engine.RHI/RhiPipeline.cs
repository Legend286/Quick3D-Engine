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
        RhiNative.TextureFormat colorFormat, bool enableDepth = true, bool enableDepthWrite = true, bool enableBlend = false, RhiNative.PrimitiveTopology topology = RhiNative.PrimitiveTopology.TriangleList)
    {
        var desc = new RhiNative.GraphicsPipelineDesc
        {
            Abi = 3, // Bumped ABI version
            VertexShader = vertexShader.Handle,
            FragmentShader = fragmentShader.Handle,
            ColorFormat = colorFormat,
            EnableDepth = enableDepth ? 1 : 0,
            EnableDepthWrite = enableDepthWrite ? 1 : 0,
            EnableBlend = enableBlend ? 1 : 0,
            SampleCount = 1,
            PrimitiveTopology = (uint)topology
        };
        int res = RhiNative.RhiCreateGraphicsPipeline(device.Handle, in desc, out IntPtr handle);
        if (res != 0 || handle == IntPtr.Zero)
            throw new Exception("Failed to create graphics pipeline.");
        return new RhiPipeline(handle);
    }

    public static RhiPipeline CreateCompute(RhiDevice device, RhiShader computeShader)
    {
        var desc = new RhiNative.ComputePipelineDesc
        {
            Abi = 2,
            ComputeShader = computeShader.Handle
        };
        int rc = RhiNative.RhiCreateComputePipeline(device.Handle, in desc, out IntPtr p);
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

    /// <summary>Safety net: see <see cref="RhiBuffer"/>.</summary>
    ~RhiPipeline() => Dispose();
}
