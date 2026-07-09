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
//   scheme=2  → Zstandard supercompression using the legacy Khronos identifier
//               (the value that current `basisu -ktx2 -uastc` defaults to).
//               Per the current Khronos KTX2 spec, scheme=2 is defined as a
//               deprecated alias for scheme=3 and decoded identically. We
//               route it through the same Zstd path as scheme=3.
//   scheme=3  → Zstandard supercompression (current Khronos identifier).
//               Each mip's level index entry holds its own compressed +
//               uncompressed sizes, so we Zstd-decompress per mip before
//               upload.
//   scheme=1  → Basis Universal ETC1S transcodable. Not supported here; the
//               Cook avoids this path via `-uastc` (which targets ASTC blocks
//               directly instead of going through the ETC1S path).
//
// Format-registry mapping (Khronos format-registry ids used as the
// KTX2/BASIS/DDS file-header identifier) is owned by the RHI module — see
// Engine.RHI.RhiTexture.FromKhronosFormat. The loader does not duplicate
// the table on purpose: the same Khronos registry applies to every GPU API
// (Metal/Vulkan/DX12), so the mapping belongs in the RHI abstraction layer
// rather than alongside any single backend's decode path.
//
using System;
using System.Buffers.Binary;
using System.IO;
using Engine.CBindings;
using Engine.RHI;
using ZstdSharp;
using static Engine.CBindings.Log;

namespace Engine.Assets;

public static class Ktx2Loader
{
    private static bool _transcoderInitialized = false;

    [System.Runtime.InteropServices.DllImport("EngineC")]
    private static extern void engine_transcoder_init();

    [System.Runtime.InteropServices.DllImport("EngineC")]
    private static extern bool engine_transcode_uastc_to_astc(IntPtr uastc_data, IntPtr out_astc_data, uint block_count);

    private static ReadOnlySpan<byte> KTX2Identifier =>
        new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };

    public static RhiTexture? Load(RhiDevice device, string path)
    {
        if (!File.Exists(path))
        {
            Error($"KTX2 not found at '{path}'. Check that Cook produced the .ktx2 binary (it is required next to the .tex sidecar).", "KTX2Loader");
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            Error($"Failed to read KTX2 '{path}': {ex.Message}", "KTX2Loader");
            return null;
        }

        if (bytes.Length < 80 ||
            !bytes.AsSpan(0, 12).SequenceEqual(KTX2Identifier))
        {
            Error($"Not a KTX2 file: '{path}' (missing identifier or truncated). Re-cook via Cook or verify the source asset.", "KTX2Loader");
            return null;
        }

        var span = bytes.AsSpan();
        uint vkFormat      = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));
        uint pixelWidth    = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20, 4));
        uint pixelHeight   = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(24, 4));
        uint levelCount    = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(40, 4));
        uint supercompress = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(44, 4));
        uint indexOffset   = 80; // Level Index array immediately follows the 80-byte header
        if (supercompress != 0 && supercompress != 2 && supercompress != 3)
        {
            string schemeName = supercompress switch
            {
                1 => "BasisLZ (ETC1S transcodable)",
                _ => $"unknown ({supercompress})",
            };
            Error(
                $"KTX2 supercompression={schemeName} not supported by this loader. " +
                $"Re-encode with `basisu -ktx2 -uastc` so vkFormat is a known block " +
                $"format and supercompression is 0 (none) or 2/3 (Zstd). file={path}",
                "KTX2Loader");
            return null;
        }

        bool isUastc = false;
        if (vkFormat == 0)
        {
            // basisu correctly outputs vkFormat=VK_FORMAT_UNDEFINED (0) for universal formats
            // like UASTC. Since we already rejected supercompress == 1 (BasisLZ / ETC1S),
            // this must be UASTC. The engine treats UASTC as ASTC_4x4_UNORM_BLOCK (157).
            vkFormat = 157;
            isUastc = true;
            if (!_transcoderInitialized)
            {
                engine_transcoder_init();
                _transcoderInitialized = true;
            }
        }

        if (!RhiTexture.FromKhronosFormat(vkFormat, out var rhiFormat, out string fmtName))
        {
            Error($"KTX2 has unsupported vkFormat={vkFormat} (file={path}). Add a mapping in RhiTexture.FromKhronosFormat.", "KTX2Loader");
            return null;
        }

        var blockInfo = RhiTexture.GetBlockInfo(rhiFormat);
        if (!blockInfo.IsBlockCompressed)
        {
            Error(
                $"KTX2 vkFormat={vkFormat} mapped to {rhiFormat} which is not block-compressed; " +
                $"uncompressed KTX2 textures should use rhi_texture_upload, not the per-mip path. file={path}",
                "KTX2Loader");
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
            Error($"Failed to create KTX2 texture ({fmtName}) for '{path}': {ex.Message}", "KTX2Loader");
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
                Error(
                    $"KTX2 level {level} extends past EOF (offset {byteOffset} + length {byteLength} > {bytes.Length}). file={path}",
                    "KTX2Loader");
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
                    if (supercompress == 2 || supercompress == 3)
                    {
                        // Zstd-supercompressed mip (scheme=2 legacy Khronos
                        // identifier OR scheme=3 current identifier): decompress
                        // before upload.
                        byte[] uncomp = uncompressedLength > 0
                            ? new byte[uncompressedLength]
                            : new byte[byteLength * 4]; // defensive fallback
                        using var dec = new Decompressor();
                        int written = dec.Unwrap(span.Slice((int)byteOffset, (int)byteLength), uncomp);
                        if (uncompressedLength > 0 && (ulong)written != uncompressedLength)
                        {
                            Error(
                                $"KTX2 Zstd level {level}: decompressed size {written} ≠ expected {uncompressedLength}. file={path}",
                                "KTX2Loader");
                            tex.Dispose();
                            return null;
                        }
                        fixed (byte* p = uncomp)
                        {
                            if (isUastc)
                            {
                                byte[] astc = new byte[uncomp.Length];
                                fixed (byte* pAstc = astc)
                                {
                                    if (!engine_transcode_uastc_to_astc((IntPtr)p, (IntPtr)pAstc, (uint)(uncomp.Length / 16)))
                                    {
                                        Error($"UASTC to ASTC transcode failed for level {level}. file={path}", "KTX2Loader");
                                        tex.Dispose();
                                        return null;
                                    }
                                    
                                    if (level == 0) {
                                        string uastcHex = BitConverter.ToString(uncomp, 0, Math.Min(16, uncomp.Length));
                                        string astcHex = BitConverter.ToString(astc, 0, Math.Min(16, astc.Length));
                                        Info($"KTX2 Level 0 First Block: UASTC={uastcHex} -> ASTC={astcHex}", "KTX2Loader");
                                        
                                        // Check if entirely black (all zeros)
                                        bool allZero = true;
                                        for(int i = 0; i < astc.Length; i++) if (astc[i] != 0) { allZero = false; break; }
                                        if (allZero) {
                                            Error("Transcoded ASTC buffer is entirely zero!", "KTX2Loader");
                                        }
                                    }
                                    tex.UploadMip(level, (IntPtr)pAstc, (ulong)astc.Length, stride);
                                }
                            }
                            else
                            {
                                tex.UploadMip(level, (IntPtr)p, (ulong)uncomp.Length, stride);
                            }
                        }
                    }
                    else
                    {
                        // scheme=0 uncompressed
                        fixed (byte* p = &bytes[(int)byteOffset])
                        {
                            if (isUastc)
                            {
                                byte[] astc = new byte[byteLength];
                                fixed (byte* pAstc = astc)
                                {
                                    if (!engine_transcode_uastc_to_astc((IntPtr)p, (IntPtr)pAstc, (uint)(byteLength / 16)))
                                    {
                                        Error($"UASTC to ASTC transcode failed for level {level}. file={path}", "KTX2Loader");
                                        tex.Dispose();
                                        return null;
                                    }
                                    tex.UploadMip(level, (IntPtr)pAstc, byteLength, stride);
                                }
                            }
                            else
                            {
                                tex.UploadMip(level, (IntPtr)p, byteLength, stride);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Error($"KTX2 level {level} upload failed ({fmtName}) for '{path}': {ex.Message}", "KTX2Loader");
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

}
