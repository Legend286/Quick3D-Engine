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
        RhiNative.RhiDestroyPipeline(Handle);
        GC.SuppressFinalize(this);
    }
}
