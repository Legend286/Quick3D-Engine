// SPDX-License-Identifier: MIT
using System;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiHeap : IDisposable
{
    public IntPtr Handle { get; private set; }
    public bool IsInitialized => Handle != IntPtr.Zero;

    public RhiHeap(RhiDevice device, ulong size, uint usageFlags)
    {
        var desc = new RhiNative.HeapDesc
        {
            Size = size,
            UsageFlags = usageFlags,
        };
        int rc = RhiNative.RhiCreateHeap(device.Handle, in desc, out IntPtr h);
        if (rc != 0) throw new InvalidOperationException($"rhi_create_heap failed: {rc}");
        Handle = h;
    }

    public RhiTexture CreateTexture(RhiDevice device, in RhiNative.TextureDesc desc, ulong offset)
    {
        int rc = RhiNative.RhiCreateTextureFromHeap(device.Handle, Handle, in desc, offset, out IntPtr tex);
        if (rc != 0) throw new InvalidOperationException($"rhi_create_texture_from_heap failed: {rc}");
        return new RhiTexture(tex, ownsHandle: true);
    }

    public RhiBuffer CreateBuffer(RhiDevice device, in RhiNative.BufferDesc desc, ulong offset)
    {
        int rc = RhiNative.RhiCreateBufferFromHeap(device.Handle, Handle, in desc, offset, out IntPtr buf);
        if (rc != 0) throw new InvalidOperationException($"rhi_create_buffer_from_heap failed: {rc}");
        return new RhiBuffer(buf, desc.Size, ownsHandle: true);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            RhiNative.RhiDestroyHeap(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
