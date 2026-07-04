// SPDX-License-Identifier: MIT
// KTX2 file parser for compressed GPU textures.
//
// KTX2 v2 header layout (per Khronos KTX-Specification):
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
//   bytes 48-51 : keyValueByteOffset      (0 when no key-value data)
//   bytes 52-55 : keyValueByteLength
//   bytes 56-59 : dataFormatDescriptorByteOffset (= 0 when no DFD)
//   bytes 60-63 : dataFormatDescriptorByteLength
//   bytes 64-67 : firstLevelOffset        (level index, 0 = extends past KVD/DFD/SGD)
//   bytes 68-71 : supercompressionGlobalDataByteOffset
//   bytes 72-75 : supercompressionGlobalDataByteLength
//   bytes 76-79 : header padding
//   bytes 80+   : KVD / DFD / level index / level data, in writer-defined order
// See https://registry.khronos.org/KTX/specs/2.0/ktxspec.v2.html for the
// full header spec. basisu's KTX writer conforms to the field positions
// but uses a non-standard DFD payload (no KHR_DF colorModel byte, only a
// vendor string) — see vkFormat==0 DFD recovery branch below.
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
        uint vkFormat        = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12, 4));
        uint pixelWidth      = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(20, 4));
        uint pixelHeight     = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(24, 4));
        uint levelCount      = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(40, 4));
        uint supercompress   = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(44, 4));
        uint kvByteOffset    = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(48, 4));
        uint kvByteLength    = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(52, 4));
        uint dfdByteOffset   = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(56, 4));
        uint dfdByteLength   = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(60, 4));
        uint firstLevelOffset = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(64, 4));
        uint sgdByteOffset   = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(68, 4));
        uint sgdByteLength   = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(72, 4));

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

        // VK_FORMAT_UNDEFINED (vkFormat=0) + a DFD that resolves to UASTC
        // is the Khronos KTX2 convention for Basis Universal transcodable
        // UASTC. `basisu -ktx2 -uastc` writes this shape because UASTC is
        // fundamentally a transcoder container; the 16-byte UASTC blocks
        // are ASTC 4x4-compatible on any GPU with ASTC support, so we
        // recover to that physical block format (id 157,
        // VK_FORMAT_ASTC_4x4_UNORM_BLOCK) rather than failing the load.
        // The Khronos-format-registry table stays the single source of
        // truth (RhiTexture.FromKhronosFormat maps id 157 to
        // Astc4x4UnormBlock); we override fmtName so any downstream log
        // identifies the UASTC source.
        //
        // DFD detection accepts both shapes the engine sees in practice:
        //   1. Standard-conformant writers + future basisu — any byte
        //      == 0xA6 (KHR_DF_MODEL_UASTC) anywhere in
        //      [dfd_off, dfd_off+dfd_len). The Khronos spec puts
        //      colorModel at DFD+8; basisu ≥ v2.10 emits additional
        //      vendor-prefix bytes that shift this to DFD+12 (see the
        //      pan::_kh_df instruction block). The DFD is small
        //      (typ. 32-128 bytes) so scanning the whole block is free.
        //   2. basisu v2.10's non-conformant DFD where the first 4 bytes
        //      are `1f 00 00 00` (descriptorBlockSize-1 length prefix
        //      per KHR_DF_BLOCK) immediately followed by the ASCII vendor
        //      string "KTXwriter\0Basis Universal <version>\0". We
        //      require BOTH the "KTXw" prefix at DFD+4 AND a "Basis
        //      Universal" substring anywhere in the first 64 bytes so
        //      arbitrary files that happen to contain 0xA6 elsewhere
        //      don't false-positive standard-conformance.
        RhiNative.TextureFormat rhiFormat;
        string fmtName;

        if (vkFormat == 0)
        {
            if (IsUastcDfd(span, dfdByteOffset, dfdByteLength))
            {
                RhiTexture.FromKhronosFormat(157, out rhiFormat, out fmtName);
                fmtName = "UASTC_4x4 (DFD colorModel=166)";
            }
            else
            {
                string recipe = supercompress == 1
                    ? "vkFormat=0 + supercompression=1 (BasisLZ/ETC1S) means Cook most likely treated the source as ETC1S instead of UASTC. Re-import the source asset through the editor's Asset Import; if the same error returns, examine Cook/main.cpp::ExecuteBasisu to confirm `-uastc` is in the basisu invocation. Delete any stale .ktx2 / .tex on disk before re-importing so the new Cook writes fresh."
                    : "vkFormat=0 with no DFD colorModel=166 (KHR_DF_MODEL_UASTC) marker and no basisu v2.10 'KTXwriter/Basis Universal' vendor identifier means the source is either an ETC1S transcodable file (rejected), a Basis Universal HDR / XUASTC variant not supported by the runtime, or a malformed cook. Re-import the source asset / delete stale sidecars before re-importing. The canonical Khronos-id table is Engine.RHI.RhiTexture.FromKhronosFormat.";
                Error($"KTX2 vkFormat=VK_FORMAT_UNDEFINED (0) is not mapped to an RHI block format. {recipe} file={path}", "KTX2Loader");
                return null;
            }
        }
        else if (!RhiTexture.FromKhronosFormat(vkFormat, out rhiFormat, out fmtName))
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
        // Per Khronos spec the table starts at `firstLevelOffset` (header
        // bytes 64-67). basisu v2.10 writes `firstLevelOffset=0` and omits
        // the index entirely; in that case we compute `dataStart` as the
        // first byte AFTER all metadata blocks (KVD, DFD, SGD) in
        // writer-defined order and decode a single contiguous Zstd stream
        // for the only mip basisu produces.
        ulong kvEnd  = (ulong)kvByteOffset + kvByteLength;
        ulong dfdEnd = (ulong)dfdByteOffset + dfdByteLength;
        ulong sgdEnd = (ulong)sgdByteOffset + sgdByteLength;
        ulong dataStart = Math.Max(80UL, Math.Max(Math.Max(kvEnd, dfdEnd), sgdEnd));
        bool hasIndexTable = firstLevelOffset >= 80
            && firstLevelOffset + (ulong)levelCount * 24 <= (ulong)bytes.Length;

        for (uint level = 0; level < levelCount; ++level)
        {
            ulong byteOffset = 0, byteLength = 0, uncompressedLength = 0;
            if (hasIndexTable)
            {
                int entryOffset = (int)(firstLevelOffset + level * 24);
                byteOffset         = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(entryOffset, 8));
                byteLength         = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(entryOffset + 8, 8));
                uncompressedLength = BinaryPrimitives.ReadUInt64LittleEndian(span.Slice(entryOffset + 16, 8));
            }
            else
            {
                // basisu v2.10 no-index fallback: assume single-mip
                // (basisu does not write mip chains) and span the rest of
                // the file as one Zstd stream. Multi-mip files without an
                // explicit index can't safely be parsed — reject those.
                if (levelCount > 1)
                {
                    Error(
                        $"KTX2 firstLevelOffset=0 (no level index) but levelCount={levelCount}; multi-mip without explicit per-mip offsets cannot be parsed safely. The Cook invoked a writer that didn't emit a level index; bump `basis_universal` to a spec-conformant version. file={path}",
                        "KTX2Loader");
                    tex.Dispose();
                    return null;
                }
                byteOffset = dataStart;
                byteLength = (ulong)bytes.Length - dataStart;
                uncompressedLength = ComputeLevelSize(
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
                        // before upload. uncompressedByteLength is sourced
                        // from the level index entry (standard path) or
                        // computed via ComputeLevelSize (basisu fallback),
                        // so it's always set when we get here. Reject
                        // unrealised zero lengths loudly rather than
                        // silently allocating a wrong-size buffer.
                        if (uncompressedLength == 0)
                        {
                            Error(
                                $"KTX2 Zstd level {level}: uncompressedByteLength=0; index entry or fallback math is wrong. file={path}",
                                "KTX2Loader");
                            tex.Dispose();
                            return null;
                        }
                        byte[] uncomp = new byte[uncompressedLength];
                        using var dec = new Decompressor();
                        int written = dec.Unwrap(span.Slice((int)byteOffset, (int)byteLength), uncomp);
                        if ((ulong)written != uncompressedLength)
                        {
                            Error(
                                $"KTX2 Zstd level {level}: decompressed size {written} ≠ expected {uncompressedLength}. file={path}",
                                "KTX2Loader");
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

    private const byte KhronosModelUastc = 0xA6; // KHR_DF_MODEL_UASTC = 166

    /// <summary>
    /// Returns true if <paramref name="span"/>'s Data Format Descriptor
    /// block at <c>[dfdByteOffset, dfdByteOffset+dfdByteLength)</c> names
    /// the underlying texture as UASTC (a Basis Universal container
    /// that transcodes to ASTC 4x4). The detection accepts both
    /// standard-conformant writers and basisu v2.10+'s non-conformant
    /// "KTXwriter\0Basis Universal" vendor string. See the format-detection
    /// rationale in this loader's header block.
    /// </summary>
    private static bool IsUastcDfd(ReadOnlySpan<byte> span, uint dfdByteOffset, uint dfdByteLength)
    {
        if (dfdByteOffset == 0 || dfdByteLength == 0) return false;
        if ((ulong)dfdByteOffset + dfdByteLength > (ulong)span.Length) return false;

        var dfd = span.Slice((int)dfdByteOffset, (int)dfdByteLength);

        // Path 1: Khronos-spec colorModel byte at any DFD offset.
        foreach (var b in dfd)
        {
            if (b == KhronosModelUastc) return true;
        }

        // Path 2: basisu v2.10 non-conformant DFD — descriptorBlockSize-1
        // length prefix immediately followed by ASCII "KTXwriter\0Basis
        // Universal <ver>\0". Require both the "KTXw" 4-byte prefix at
        // DFD+4 AND a "Basis Universal" substring in the first 64 bytes
        // to avoid spurious acceptance of arbitrary files that happen to
        // contain either pattern alone.
        if (dfd.Length >= 8 &&
            dfd[0] == 0x1F && dfd[1] == 0 && dfd[2] == 0 && dfd[3] == 0)
        {
            ReadOnlySpan<byte> ktxw = stackalloc byte[] { 0x4B, 0x54, 0x58, 0x77 }; // "KTXw"
            if (dfd.Slice(4, 4).SequenceEqual(ktxw))
            {
                var head = dfd.Length > 64 ? dfd.Slice(0, 64) : dfd;
                ReadOnlySpan<byte> basis = "Basis Universal"u8;
                return head.IndexOf(basis) >= 0;
            }
        }

        return false;
    }

}
