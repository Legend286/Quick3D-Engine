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

    /// <summary>
    /// Translate a Khronos format-registry id (the integer used inside
    /// KTX2, BASIS, and DDS file headers) into the engine's RHI-neutral
    /// <see cref="RhiNative.TextureFormat"/> enum. Returns false when the
    /// registry id has no RHI equivalent yet — caller decides whether to
    /// log, fail the load, or fall back to a placeholder texture.
    /// </summary>
    /// <remarks>
    /// Cross-platform note: the Khronos format-registry id space is the
    /// universal file-format id across KTX2, BASIS, and DDS regardless of
    /// which GPU API is in use at runtime (Metal/Vulkan/DX12). The table
    /// below is the project's supported-id list. New formats are added
    /// here when a backend implements them — backends and the file-loader
    /// couple on this single switch rather than duplicating knowledge in
    /// the loader. Master registry:
    /// https://registry.khronos.org/vulkan/specs/1.3-extensions/man/html/VkFormat.html
    /// (the registry is named after the Vulkan spec but applies uniformly
    /// to every compatible backend).
    /// </remarks>
    public static bool FromKhronosFormat(uint formatId,
                                         out RhiNative.TextureFormat rhiFormat,
                                         out string name)
    {
        switch (formatId)
        {
            case 12:  rhiFormat = RhiNative.TextureFormat.Bc1RgbUnormBlock;   name = "BC1_RGB_UNORM";   return true;
            case 14:  rhiFormat = RhiNative.TextureFormat.Bc1RgbaUnormBlock;  name = "BC1_RGBA_UNORM";  return true;
            case 23:  rhiFormat = RhiNative.TextureFormat.Bc3UnormBlock;      name = "BC3_UNORM";       return true;
            case 27:  rhiFormat = RhiNative.TextureFormat.Bc5UnormBlock;      name = "BC5_UNORM";       return true;
            case 42:  rhiFormat = RhiNative.TextureFormat.Bc7UnormBlock;      name = "BC7_UNORM";       return true;
            case 60:  rhiFormat = RhiNative.TextureFormat.Etc2Rgb8UnormBlock; name = "ETC2_RGB8_UNORM"; return true;
            case 157: rhiFormat = RhiNative.TextureFormat.Astc4x4UnormBlock;  name = "ASTC_4x4_UNORM";  return true;
            default:
                rhiFormat = RhiNative.TextureFormat.Undefined;
                name = "?";
                return false;
        }
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
