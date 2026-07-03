// SPDX-License-Identifier: MIT
// Managed texture wrapper; CPU readback via Metal getBytes.

using System;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiTexture : IDisposable
{
    public IntPtr Handle { get; private set; }
    private readonly bool _owns;

    internal RhiTexture(IntPtr handle, bool ownsHandle)
    {
        Handle = handle;
        _owns = ownsHandle;
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

    public static RhiTexture Create2D(RhiDevice device, uint w, uint h,
                                      RhiNative.TextureFormat format)
    {
        return CreateWithMips(device, w, h, format, 1);
    }

    public static RhiTexture CreateWithMips(RhiDevice device, uint w, uint h,
                                              RhiNative.TextureFormat format,
                                              uint mipLevels)
    {
        var desc = new RhiNative.TextureDesc
        {
            Abi = 1,
            Width = w,
            Height = h,
            MipLevels = mipLevels,
            Format = format,
            UsageFlags = RhiNative.TextureShaderRead,
        };
        int rc = RhiNative.RhiCreateTexture(device.Handle, in desc, out IntPtr tex);
        if (rc != 0) throw new InvalidOperationException($"rhi_create_texture rc={rc}");
        return new RhiTexture(tex, ownsHandle: true);
    }

    public void Upload(IntPtr bytes, ulong size, uint stride)
    {
        int rc = RhiNative.RhiTextureUpload(Handle, bytes, size, stride);
        if (rc != 0) throw new InvalidOperationException($"rhi_texture_upload rc={rc}");
    }

    public void UploadMip(uint mipLevel, IntPtr bytes, ulong size, uint stride)
    {
        int rc = RhiNative.RhiTextureUploadMip(Handle, mipLevel, bytes, size, stride);
        if (rc != 0) throw new InvalidOperationException($"rhi_texture_upload_mip rc={rc}");
    }

    public readonly struct BlockInfo
    {
        public readonly uint BlockWidth;
        public readonly uint BlockHeight;
        public readonly uint BytesPerBlock;
        public BlockInfo(uint w, uint h, uint b) { BlockWidth = w; BlockHeight = h; BytesPerBlock = b; }
        public bool IsBlockCompressed => BlockWidth > 0 && BlockHeight > 0 && BytesPerBlock > 0;
    }

    public static BlockInfo GetBlockInfo(RhiNative.TextureFormat fmt)
    {
        RhiNative.RhiFormatBlockInfo(fmt, out uint w, out uint h, out uint b);
        return new BlockInfo(w, h, b);
    }

    public static RhiTexture CreateDepth(RhiDevice device, uint w, uint h)
    {
        var desc = new RhiNative.TextureDesc
        {
            Abi = 1,
            Width = w,
            Height = h,
            MipLevels = 1,
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
