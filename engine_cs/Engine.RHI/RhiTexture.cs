// SPDX-License-Identifier: MIT
// Managed texture wrapper; CPU readback via Metal getBytes.

using System;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiTexture : IDisposable
{
    public IntPtr Handle { get; }
    private readonly bool _owns;

    internal RhiTexture(IntPtr handle, bool ownsHandle)
    {
        Handle = handle;
        _owns  = ownsHandle;
    }

    public static RhiTexture CreateRenderTarget(RhiDevice device, uint w, uint h,
                                                RhiNative.TextureFormat format)
    {
        var desc = new RhiNative.TextureDesc
        {
            Abi = 1,
            Width = w,
            Height = h,
            MipLevels = 1,
            Format = format,
            UsageFlags = RhiNative.TextureRenderTarget | RhiNative.TextureShaderRead,
        };
        int rc = RhiNative.RhiCreateTexture(device.Handle, in desc, out IntPtr tex);
        if (rc != 0) throw new InvalidOperationException($"rhi_create_texture rc={rc}");
        return new RhiTexture(tex, ownsHandle: true);
    }

    public static RhiTexture CreateDepth(RhiDevice device, uint w, uint h)
    {
        var desc = new RhiNative.TextureDesc
        {
            Abi = 1,
            Width = w, Height = h, MipLevels = 1,
            Format = RhiNative.TextureFormat.Depth32Float,
            UsageFlags = RhiNative.TextureRenderTarget,
        };
        int rc = RhiNative.RhiCreateTexture(device.Handle, in desc, out IntPtr tex);
        if (rc != 0) throw new InvalidOperationException($"rhi_depth_create rc={rc}");
        return new RhiTexture(tex, ownsHandle: true);
    }

    /// <summary>Read back the texture's bytes into a managed byte[].
    /// Caller is responsible for choosing the row stride.</summary>
    public byte[] Readback(uint width, uint height, uint rowStrideBytes)
    {
        byte[] buffer = new byte[rowStrideBytes * height];
        int rc;
        unsafe
        {
            fixed (byte* p = buffer)
            {
                rc = RhiNative.RhiTextureReadback(Handle, (IntPtr)p,
                                                    (ulong)buffer.Length, rowStrideBytes);
            }
        }
        if (rc != 0) throw new InvalidOperationException($"rhi_texture_readback rc={rc}");
        return buffer;
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero || !_owns) return;
        // Zero the Handle field BEFORE invoking the native destroy so a
        // failed/partial C-side free doesn't get repeated by the finalizer.
        var h = Handle;
        Handle = IntPtr.Zero;
        RhiNative.RhiDestroyTexture(h);
        GC.SuppressFinalize(this);
    }

    /// <summary>Safety net: see <see cref="RhiBuffer"/>.</summary>
    ~RhiTexture() => Dispose();
}
