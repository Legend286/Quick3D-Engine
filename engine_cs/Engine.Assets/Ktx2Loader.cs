// SPDX-License-Identifier: MIT
// KTX2 file parser for compressed GPU textures.
//
// KTX2 header (see Khronos KTX-Specification for full details):
//   bytes 0-11  : Identifier (12 bytes)
//   bytes 12-15 : vkFormat (uint32, Vulkan format enum)
//   bytes 16-19 : typeSize  (always 1 for color images)
//   bytes 20-23 : pixelWidth
//   bytes 24-27 : pixelHeight
//   bytes 28-31 : pixelDepth (0 = flat 2D)
//   bytes 32-35 : layerCount (0 = base image only)
//   bytes 36-39 : faceCount  (1 = cubemap face)
//   bytes 40-43 : levelCount (mip levels)
//   bytes 44-47 : supercompressionScheme (0=None, 1=BasisLZ, 3=Zstd)
//   bytes 48-51 : index (byte offset to level index, often 80)
//   ...
//   bytes 80    : level index (levelCount entries, each 24 bytes: byteOffset, byteLength, uncompressedByteLength)
//
// Supercompression support:
//   scheme=0  → Uncompressed block data; uploaded directly via UploadMip.
//   scheme=3  → Zstandard supercompression; each mip's level index entry
//               holds its own compressed + uncompressed sizes, so we
//               Zstd-decompress per mip before upload. This is what the
//               `Cook` step emits when it runs `basisu -ktx2 -uastc`, which
//               targets ASTC blocks and Zstd-supercompresses them by default.
//   scheme=1  → Basis Universal ETC1S transcodable. Not supported here; the
//               Cook avoids this path via `-uastc` (which targets ASTC blocks
//               directly instead of going through the ETC1S path).
//
// VkFormat mapping (per Khronos Vulkan format registry):
//    12 = VK_FORMAT_BC1_RGB_UNORM_BLOCK
//    14 = VK_FORMAT_BC1_RGBA_UNORM_BLOCK
//    23 = VK_FORMAT_BC3_UNORM_BLOCK
//    27 = VK_FORMAT_BC5_UNORM_BLOCK
//    42 = VK_FORMAT_BC7_UNORM_BLOCK
//    60 = VK_FORMAT_ETC2_R8G8B8_UNORM_BLOCK
//   157 = VK_FORMAT_ASTC_4x4_UNORM_BLOCK

using System;
using System.Buffers.Binary;
using System.IO;
using Engine.CBindings;
using Engine.RHI;
using ZstdSharp;

namespace Engine.Assets;

public static class Ktx2Loader
{
    private static ReadOnlySpan<byte> KTX2Identifier =>
        new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };

    public static RhiTexture? Load(RhiDevice device, string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"KTX2 not found: {path}");
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to read KTX2 {path}: {ex.Message}");
            return null;
        }

        if (bytes.Length < 80 ||
            !bytes.AsSpan(0, 12).SequenceEqual(KTX2Identifier))
        {
            Console.WriteLine($"Not a KTX2 file: {path}");
            return null;
        }

        var span = bytes.AsSpan();
        uint vkFormat      = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));
        uint pixelWidth    = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20, 4));
        uint pixelHeight   = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(24, 4));
        uint levelCount    = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(40, 4));
        uint supercompress = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(44, 4));
        uint indexOffset   = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(48, 4));

        if (supercompress != 0 && supercompress != 3)
        {
            string schemeName = supercompress switch
            {
                1 => "BasisLZ (ETC1S transcodable)",
                2 => "Zstandard (Khronos deprecated alias)",
                _ => $"unknown ({supercompress})",
            };
            Console.WriteLine(
                $"KTX2 supercompression={schemeName} not supported by this loader. " +
                $"Re-encode with `basisu -ktx2 -uastc` so vkFormat is real and scheme=3 (Zstd), " +
                $"or `-no_zstd` for scheme=0. file={path}");
            return null;
        }

        if (!TryMapVkFormat(vkFormat, out var rhiFormat, out string fmtName))
        {
            Console.WriteLine($"KTX2 has unsupported vkFormat={vkFormat} (file={path})");
            return null;
        }

        var blockInfo = RhiTexture.GetBlockInfo(rhiFormat);
        if (!blockInfo.IsBlockCompressed)
        {
            Console.WriteLine(
                $"KTX2 vkFormat={vkFormat} mapped to {rhiFormat} which is not block-compressed; " +
                $"uncompressed KTX2 textures should use rhi_texture_upload, not the per-mip path. file={path}");
            return null;
        }

        if (levelCount == 0) levelCount = 1;

        RhiTexture tex;
        try
        {
            tex = RhiTexture.CreateWithMips(
                device,
                Math.Max(1, pixelWidth),
                Math.Max(1, pixelHeight),
                rhiFormat,
                levelCount);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to create KTX2 texture ({fmtName}) for {path}: {ex.Message}");
            return null;
        }

        // Level index entries are 24 bytes each
        // (byteOffset u64, byteLength u64, uncompressedByteLength u64).
        // For supercompressionScheme=3 the level index is mandatory per the
        // KTX2 spec; for scheme=0 it MAY be omitted.
        ulong dataStart = 80;
        bool hasIndexTable = indexOffset >= 80 && (indexOffset + levelCount * 24) <= (ulong)bytes.Length;

        for (uint level = 0; level < levelCount; ++level)
        {
            ulong byteOffset = 0, byteLength = 0, uncompressedLength = 0;
            if (hasIndexTable)
            {
                int entryOffset = (int)(indexOffset + level * 24);
                byteOffset         = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(entryOffset, 8));
                byteLength         = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(entryOffset + 8, 8));
                uncompressedLength = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(entryOffset + 16, 8));
            }
            else
            {
                byteOffset = dataStart;
                byteLength = ComputeLevelSize(
                    Math.Max(1, pixelWidth  >> (int)level),
                    Math.Max(1, pixelHeight >> (int)level),
                    blockInfo);
            }

            if (byteLength == 0)
            {
                byteLength = ComputeLevelSize(
                    Math.Max(1, pixelWidth  >> (int)level),
                    Math.Max(1, pixelHeight >> (int)level),
                    blockInfo);
            }

            if (byteOffset + byteLength > (ulong)bytes.Length)
            {
                Console.WriteLine(
                    $"KTX2 level {level} extends past EOF (offset {byteOffset} + length {byteLength} > {bytes.Length}). file={path}");
                tex.Dispose();
                return null;
            }

            uint levelW = Math.Max(1u, pixelWidth  >> (int)level);
            uint levelH = Math.Max(1u, pixelHeight >> (int)level);
            uint stride = StrideForLevel(levelW, levelH, blockInfo);

            unsafe
            {
                try
                {
                    if (supercompress == 3)
                    {
                        // Zstd-supercompressed mip: decompress before upload.
                        byte[] uncomp = uncompressedLength > 0
                            ? new byte[uncompressedLength]
                            : new byte[byteLength * 4]; // defensive fallback
                        using var dec = new Decompressor();
                        int written = dec.Unwrap(span.Slice((int)byteOffset, (int)byteLength), uncomp);
                        if (uncompressedLength > 0 && (ulong)written != uncompressedLength)
                        {
                            Console.WriteLine(
                                $"KTX2 Zstd level {level}: decompressed size {written} ≠ expected {uncompressedLength}. file={path}");
                            tex.Dispose();
                            return null;
                        }
                        fixed (byte* p = uncomp)
                        {
                            tex.UploadMip(level, (IntPtr)p, (ulong)uncomp.Length, stride);
                        }
                    }
                    else
                    {
                        // scheme=0 uncompressed
                        fixed (byte* p = &bytes[(int)byteOffset])
                        {
                            tex.UploadMip(level, (IntPtr)p, byteLength, stride);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"KTX2 level {level} upload failed ({fmtName}): {ex.Message}");
                    tex.Dispose();
                    return null;
                }
            }

            if (!hasIndexTable)
            {
                dataStart = byteOffset + byteLength;
            }
        }

        return tex;
    }

    private static ulong ComputeLevelSize(uint levelW, uint levelH, RhiTexture.BlockInfo block)
    {
        uint blocksW = Math.Max(1u, (levelW + block.BlockWidth  - 1) / block.BlockWidth);
        uint blocksH = Math.Max(1u, (levelH + block.BlockHeight - 1) / block.BlockHeight);
        return (ulong)(blocksW * blocksH * block.BytesPerBlock);
    }

    private static uint StrideForLevel(uint levelW, uint levelH, RhiTexture.BlockInfo block)
    {
        uint blocksW = Math.Max(1u, (levelW + block.BlockWidth - 1) / block.BlockWidth);
        return blocksW * block.BytesPerBlock;
    }

    private static bool TryMapVkFormat(uint vkFormat,
                                        out RhiNative.TextureFormat rhiFormat,
                                        out string name)
    {
        switch (vkFormat)
        {
            case 12:  rhiFormat = RhiNative.TextureFormat.Bc1RgbUnormBlock;       name = "BC1_RGB_UNORM";   return true;
            case 14:  rhiFormat = RhiNative.TextureFormat.Bc1RgbaUnormBlock;      name = "BC1_RGBA_UNORM";  return true;
            case 23:  rhiFormat = RhiNative.TextureFormat.Bc3UnormBlock;          name = "BC3_UNORM";       return true;
            case 27:  rhiFormat = RhiNative.TextureFormat.Bc5UnormBlock;          name = "BC5_UNORM";       return true;
            case 42:  rhiFormat = RhiNative.TextureFormat.Bc7UnormBlock;          name = "BC7_UNORM";       return true;
            case 60:  rhiFormat = RhiNative.TextureFormat.Etc2Rgb8UnormBlock;     name = "ETC2_RGB8_UNORM"; return true;
            case 157: rhiFormat = RhiNative.TextureFormat.Astc4x4UnormBlock;      name = "ASTC_4x4_UNORM";  return true;
            default:
                rhiFormat = RhiNative.TextureFormat.Undefined;
                name = "?";
                return false;
        }
    }
}
