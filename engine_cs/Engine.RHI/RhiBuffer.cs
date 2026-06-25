// SPDX-License-Identifier: MIT
// Managed buffer wrapper.

using System;
using System.Runtime.InteropServices;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiBuffer : IDisposable
{
    public IntPtr Handle { get; }
    public ulong Size { get; }

    internal RhiBuffer(IntPtr handle, ulong size)
    {
        Handle = handle;
        Size   = size;
    }

    public static RhiBuffer Create(RhiDevice device, ulong size, RhiNative.BufferUsage usage)
    {
        var desc = new RhiNative.BufferDesc
        {
            Abi = 1,
            Size = size,
            Usage = usage,
        };
        int rc = RhiNative.RhiCreateBuffer(device.Handle, in desc, out IntPtr buf);
        if (rc != 0) throw new InvalidOperationException($"rhi_create_buffer rc={rc}");
        return new RhiBuffer(buf, size);
    }

    public void Upload<T>(ReadOnlySpan<T> data) where T : unmanaged
    {
        if (data.Length == 0) return;
        unsafe
        {
            fixed (T* p = data)
            {
                int rc = RhiNative.RhiBufferUpload(Handle, (IntPtr)p,
                                                    (ulong)(data.Length * sizeof(T)));
                if (rc != 0) throw new InvalidOperationException($"rhi_buffer_upload rc={rc}");
            }
        }
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        RhiNative.RhiDestroyBuffer(Handle);
        GC.SuppressFinalize(this);
    }
}
