// SPDX-License-Identifier: MIT
// Pipeline wrapper.

using System;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiPipeline : IDisposable
{
    public IntPtr Handle { get; }

    internal RhiPipeline(IntPtr handle) { Handle = handle; }

    public static RhiPipeline CreateGraphics(RhiDevice device,
                                              RhiShader vertex,
                                              RhiShader fragment,
                                              RhiNative.TextureFormat colorFormat,
                                              bool enableDepth)
    {
        var desc = new RhiNative.GraphicsPipelineDesc
        {
            Abi = 1,
            VertexShader = vertex.Handle,
            FragmentShader = fragment.Handle,
            ColorFormat = colorFormat,
            EnableDepth = enableDepth ? 1 : 0,
            SampleCount = 1,
        };
        int rc = RhiNative.RhiCreateGraphicsPipeline(device.Handle, in desc, out IntPtr p);
        if (rc != 0) throw new InvalidOperationException($"rhi_create_graphics_pipeline rc={rc}");
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
