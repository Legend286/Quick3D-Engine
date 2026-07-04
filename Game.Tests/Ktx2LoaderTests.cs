// SPDX-License-Identifier: MIT
using System;
using System.Buffers.Binary;
using System.IO;
using Engine.Assets;
using Engine.RHI;
using Xunit;
using ZstdSharp;

namespace Engine.Game.Tests;

/// <summary>
/// Round-trip tests for the KTX2 compressed-texture loader. We forge valid
/// KTX2 byte streams in-memory (deterministic, no basisu dependency) so the
/// tests exercise the full parse → scheme handle → Zstd decompress →
/// per-mip upload chain without needing a real cooked texture on disk.
/// See docs/asset-pipeline/ktx2.md for the KTX2 header layout.
/// </summary>
public sealed class Ktx2LoaderTests
{
    private static readonly byte[] Ktx2Ident =
        { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>
    /// Forge a 4x4 BC1 (DXT1) KTX2 with the given supercompression scheme.
    /// The single mip is a solid-black BC1 block (8 zero bytes). Scheme 0
    /// stores the block raw; scheme 3 wraps it through Zstandard. Layout:
    ///   [0, 80)   fixed header (12-byte magic + 36-byte descriptors)
    ///   [80, 104) level-0 index entry (byteOffset, byteLength, uncompressedByteLength)
    ///   [104, end) level-0 data (raw or Zstd-compressed)
    /// </summary>
    private static byte[] ForgeBc14x4(uint supercompressScheme) =>
        ForgeBc14x4WithVkFormat(vkFormat: 12, supercompressScheme: supercompressScheme);

    /// <summary>
    /// Generic KTX2 forge used by tests that need to pin an arbitrary
    /// Khronos format-registry id (vkFormat) AND a supercompression scheme.
    /// — see Engine.RHI.RhiTexture.FromKhronosFormat for the full
    /// supported-id set. The synthetic mip data is one solid-black BC1
    /// block (8 bytes), fine for rejection-path tests (e.g. vkFormat=0 →
    /// loader refuses before reading mip data).
    /// </summary>
    private static byte[] ForgeBc14x4WithVkFormat(uint vkFormat, uint supercompressScheme)
    {
        const uint width = 4, height = 4, levelCount = 1;
        const uint indexOffset = 80;
        byte[] uncomp = new byte[8]; // one solid-black BC1 block

        byte[] comp;
        if (supercompressScheme == 2 || supercompressScheme == 3)
        {
            // Zstd compress via the explicit 2-arg Span overload that
            // returns int (bytes written). Verified to exist in
            // ZstdSharp.Port 0.7.5 (Stream API is not present there).
            byte[] dest = new byte[uncomp.Length + 64];
            using var compressor = new Compressor();
            int written = compressor.Wrap(uncomp.AsSpan(), dest.AsSpan());
            Assert.True(written > 0, $"Zstd compressor wrote {written} bytes (expected > 0)");
            comp = new byte[written];
            Buffer.BlockCopy(dest, 0, comp, 0, written);
        }
        else
        {
            comp = uncomp;
        }

        ulong dataOffset = indexOffset + 24;
        var result = new byte[(int)(dataOffset + (ulong)comp.Length)];

        Buffer.BlockCopy(Ktx2Ident, 0, result, 0, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), vkFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), 1);  // typeSize
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20, 4), width);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24, 4), height);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28, 4), 0);  // pixelDepth
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32, 4), 0);  // layerCount
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(36, 4), 1);  // faceCount
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40, 4), levelCount);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(44, 4), supercompressScheme);
        // Per Khronos spec offset 48 is keyValueByteOffset; offset 64 is
        // firstLevelOffset. Write 80 to BOTH so the level-index path is
        // exercised (legacy loader happened to read offset 48 as
        // indexOffset; new loader reads offset 64 as firstLevelOffset; both
        // happen to be 80 here so this fixture works across both code
        // generations).
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(48, 4), indexOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(64, 4), indexOffset);

        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan((int)indexOffset, 8), dataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan((int)(indexOffset + 8), 8), (ulong)comp.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan((int)(indexOffset + 16), 8), (ulong)uncomp.Length);

        Buffer.BlockCopy(comp, 0, result, (int)dataOffset, comp.Length);
        return result;
    }

    /// <summary>
    /// Intentional bootstrap pattern: create-then-dispose an RhiDevice to
    /// confirm the native backend is reachable, then let each failing test
    /// return silently. Mirrors BarrelSceneLoadTests.FullMdlLoad_* and is
    /// fine on Apple Metal where the underlying MTLDevice is shared.
    /// </summary>
    private static bool ProbeDevice()
    {
        try
        {
            using var probe = new RhiDevice();
            return probe.Handle != IntPtr.Zero;
        }
        catch (DllNotFoundException) { return false; }
        catch (Exception ex) when (ex.Message.Contains("backend")) { return false; }
    }

    /// <summary>
    /// Best-effort temp-file cleanup. Wrapping in try/catch prevents a
    /// lingering handle (AV scanner, Windows file lock) from masking the
    /// test's actual pass/fail assertion outcome.
    /// </summary>
    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* best-effort */ }
        catch (UnauthorizedAccessException) { /* best-effort */ }
    }

    [Fact]
    public void Ktx2_ForgeScheme0_LoadsIntoRhiTexture()
    {
        if (!ProbeDevice()) return;

        using var device = new RhiDevice();
        var path = Path.Combine(Path.GetTempPath(), $"forge_s0_{Guid.NewGuid():N}.ktx2");
        File.WriteAllBytes(path, ForgeBc14x4(supercompressScheme: 0));
        try
        {
            using var tex = Ktx2Loader.Load(device, path);
            Assert.NotNull(tex);
            Assert.NotEqual(IntPtr.Zero, tex!.Handle);
        }
        finally { TryDeleteFile(path); }
    }

    [Fact]
    public void Ktx2_ForgeScheme2ZstdAlias_LoadsIntoRhiTexture()
    {
        // Per the current Khronos KTX2 spec, supercompressionScheme=2 is a
        // deprecated alias for scheme=3 (Zstandard). Real Cook output is
        // currently scheme=2 because that is what `basisu -ktx2 -uastc`
        // defaults to; the loader must accept it through the same Zstd
        // decompression path as scheme=3.
        if (!ProbeDevice()) return;

        using var device = new RhiDevice();
        var path = Path.Combine(Path.GetTempPath(), $"forge_s2_{Guid.NewGuid():N}.ktx2");
        File.WriteAllBytes(path, ForgeBc14x4(supercompressScheme: 2));
        try
        {
            using var tex = Ktx2Loader.Load(device, path);
            Assert.NotNull(tex);
            Assert.NotEqual(IntPtr.Zero, tex!.Handle);
        }
        finally { TryDeleteFile(path); }
    }

    [Fact]
    public void Ktx2_ForgeScheme3Zstd_LoadsIntoRhiTexture()
    {
        if (!ProbeDevice()) return;

        using var device = new RhiDevice();
        var path = Path.Combine(Path.GetTempPath(), $"forge_s3_{Guid.NewGuid():N}.ktx2");
        File.WriteAllBytes(path, ForgeBc14x4(supercompressScheme: 3));
        try
        {
            using var tex = Ktx2Loader.Load(device, path);
            Assert.NotNull(tex);
            Assert.NotEqual(IntPtr.Zero, tex!.Handle);
        }
        finally { TryDeleteFile(path); }
    }

    [Fact]
    public void Ktx2_BadIdentifier_ReturnsNull()
    {
        if (!ProbeDevice()) return;

        using var device = new RhiDevice();
        var path = Path.Combine(Path.GetTempPath(), $"bad_id_{Guid.NewGuid():N}.ktx2");
        File.WriteAllBytes(path, new byte[128]);
        try
        {
            Assert.Null(Ktx2Loader.Load(device, path));
        }
        finally { TryDeleteFile(path); }
    }

    [Fact]
    public void Ktx2_UnsupportedScheme_ReturnsNull()
    {
        if (!ProbeDevice()) return;

        using var device = new RhiDevice();
        var path = Path.Combine(Path.GetTempPath(), $"bad_s1_{Guid.NewGuid():N}.ktx2");
        File.WriteAllBytes(path, ForgeBc14x4(supercompressScheme: 1));
        try
        {
            Assert.Null(Ktx2Loader.Load(device, path));
        }
        finally { TryDeleteFile(path); }
    }

    [Fact]
    public void Ktx2_VkFormatUndefined_ReturnsNull()
    {
        // vkFormat=0 with NO DFD colorModel=166 marker is rejected: the
        // loader surfaces the actionable diagnostic that points the caller
        // at RhiTexture.FromKhronosFormat and the Asset Import recipe.
        if (!ProbeDevice()) return;

        using var device = new RhiDevice();
        var path = Path.Combine(Path.GetTempPath(), $"vkfmt_undef_s2_{Guid.NewGuid():N}.ktx2");
        File.WriteAllBytes(path, ForgeBc14x4WithVkFormat(vkFormat: 0, supercompressScheme: 2));
        try
        {
            Assert.Null(Ktx2Loader.Load(device, path));
        }
        finally { TryDeleteFile(path); }
    }

    /// <summary>
    /// Forge a 4x4 KTX2 with vkFormat=0 (VK_FORMAT_UNDEFINED) and a DFD
    /// payload at offset 80 whose colorModel byte (DFDword2.byte0) is the
    /// given value. Mirrors what `basisu -ktx2 -uastc` actually writes in
    /// our v2.10 build: vkFormat=0 paired with a real DFD that names the
    /// block format by colorModel. Used to exercise the loader's DFD-driven
    /// recovery path.
    /// </summary>
    private static byte[] ForgeDfdKtx2(uint supercompressScheme, byte colorModel)
    {
        const uint width = 4, height = 4, levelCount = 1;
        // Place the level index at offset 80 (which the existing loader's
        // legacy indexOffset field on header byte 48 happily reads) and put
        // the DFD right after it at offset 104. This mirrors how a Khronos
        // conformant writer partitions the metadata blocks while keeping
        // the loader's existing index walk able to find the mip data.
        uint indexOffset = 80;
        uint dfdOffset = indexOffset + 24;
        uint dfdByteLength = 12;
        const uint vkFormat = 0; // VK_FORMAT_UNDEFINED — recovered via DFD

        var dfd = new byte[]
        {
            0x00, 0x00, 0x00, 0x00, // DFDword0 (vendorId=0, descriptorType=KHR_DF=0)
            0x00, 0x00, 0x00, 0x0C, // DFDword1 (version=0, descriptorBlockSize=12)
            colorModel, 0x00, 0x00, 0x00, // DFDword2 (the byte our loader reads)
        };

        // ASTC 4x4 RGBA block = 16 bytes. Real UASTC-encoded blocks land here;
        // for the test we just want the upload path to receive the right
        // byte-length and stride; we don't inspect decoded pixel values.
        byte[] uncomp = new byte[16];

        byte[] comp;
        if (supercompressScheme == 2 || supercompressScheme == 3)
        {
            byte[] dest = new byte[uncomp.Length + 64];
            using var compressor = new Compressor();
            int written = compressor.Wrap(uncomp.AsSpan(), dest.AsSpan());
            Assert.True(written > 0, $"Zstd compressor wrote {written} bytes (expected > 0)");
            comp = new byte[written];
            Buffer.BlockCopy(dest, 0, comp, 0, written);
        }
        else
        {
            comp = uncomp;
        }

        ulong dataOffset = (ulong)(dfdOffset + dfdByteLength);
        var result = new byte[(int)(dataOffset + (ulong)comp.Length)];

        Buffer.BlockCopy(Ktx2Ident, 0, result, 0, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), vkFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), 1);  // typeSize
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20, 4), width);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24, 4), height);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28, 4), 0);  // pixelDepth
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32, 4), 0);  // layerCount
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(36, 4), 1);  // faceCount
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40, 4), levelCount);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(44, 4), supercompressScheme);
        // Per Khronos spec offset 48 is keyValueByteOffset; the existing
        // loader reads it as its legacy `indexOffset` field. Both happen
        // to be 80 in this fixture so the loader's index lookup succeeds.
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(48, 4), indexOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(52, 4), 0);  // keyValueByteLength
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(56, 4), dfdOffset); // dataFormatDescriptorByteOffset
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(60, 4), dfdByteLength);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(64, 4), 0);  // firstLevelOffset (no separate index per spec)
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(68, 4), 0);  // sgdByteOffset
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(72, 4), 0);  // sgdByteLength

        // Level index entry for 1 mip at offset 80:
        //   byteOffset u64, byteLength u64, uncompressedByteLength u64
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan((int)indexOffset, 8), dataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan((int)(indexOffset + 8), 8), (ulong)comp.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan((int)(indexOffset + 16), 8), (ulong)uncomp.Length);

        Buffer.BlockCopy(dfd, 0, result, (int)dfdOffset, dfd.Length);
        Buffer.BlockCopy(comp, 0, result, (int)dataOffset, comp.Length);
        return result;
    }

    [Fact]
    public void Ktx2_DfdUastcColorModel_LoadsIntoRhiTexture()
    {
        // Standard Khronos-conformant shape: vkFormat=0 + a DFD with
        // colorModel=166 (KHR_DF_MODEL_UASTC) at DFD+8 + supercompression=2.
        // Loader must recover via the colorModel-scan branch in IsUastcDfd.
        if (!ProbeDevice()) return;

        using var device = new RhiDevice();
        var path = Path.Combine(Path.GetTempPath(), $"dfd_uastc_{Guid.NewGuid():N}.ktx2");
        File.WriteAllBytes(path, ForgeDfdKtx2(supercompressScheme: 2, colorModel: 166));
        try
        {
            using var tex = Ktx2Loader.Load(device, path);
            Assert.NotNull(tex);
            Assert.NotEqual(IntPtr.Zero, tex!.Handle);
        }
        finally { TryDeleteFile(path); }
    }

    /// <summary>
    /// Forge a 4x4 KTX2 with vkFormat=0 and a basisu v2.10 non-conformant
    /// DFD payload (length-prefix `1f 00 00 00` followed by ASCII
    /// "KTXwriter\0Basis Universal 2.10\0"). Mirrors exactly what
    /// `basisu -ktx2 -uastc` writes to disk in our current build, and
    /// drives the vendor-string detection branch of IsUastcDfd. The
    /// level index entry is placed at offset 80 followed by the DFD
    /// block (right after) so the loader's existing level-index walk
    /// can find it.
    /// </summary>
    private static byte[] ForgeBasisuV210Ktx2(uint supercompressScheme)
    {
        const uint width = 4, height = 4, levelCount = 1;
        const uint indexOffset = 80;
        const uint dfdOffset = 80 + 24;
        const string writerString = "KTXwriter\0Basis Universal 2.10\0";
        var dfd = new List<byte>();
        dfd.Add(0x1F); dfd.Add(0); dfd.Add(0); dfd.Add(0); // basisu descriptorBlockSize-1 prefix
        foreach (var c in writerString) dfd.Add((byte)c);
        dfd.Add(0); dfd.Add(0); // pad to 4-byte alignment — basisu writes 36-byte DFD total
        var dfdBytes = dfd.ToArray();

        // ASTC 4x4 block = 16 bytes. For the test we just need the upload
        // path to receive some bytes; we don't inspect decoded pixels.
        byte[] uncomp = new byte[16];
        byte[] comp;
        if (supercompressScheme == 2 || supercompressScheme == 3)
        {
            byte[] dest = new byte[uncomp.Length + 64];
            using var compressor = new Compressor();
            int written = compressor.Wrap(uncomp.AsSpan(), dest.AsSpan());
            Assert.True(written > 0, $"Zstd compressor wrote {written} bytes (expected > 0)");
            comp = new byte[written];
            Buffer.BlockCopy(dest, 0, comp, 0, written);
        }
        else { comp = uncomp; }

        ulong dataOffset = (ulong)(dfdOffset + dfdBytes.Length);
        var result = new byte[(int)(dataOffset + (ulong)comp.Length)];

        Buffer.BlockCopy(Ktx2Ident, 0, result, 0, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12, 4), 0);                 // vkFormat = VK_FORMAT_UNDEFINED
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16, 4), 1);                 // typeSize
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20, 4), width);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24, 4), height);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(28, 4), 0);                 // pixelDepth
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(32, 4), 0);                 // layerCount
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(36, 4), 1);                 // faceCount
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40, 4), levelCount);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(44, 4), supercompressScheme);
        // Per Khronos spec offset 48 is keyValueByteOffset; the existing
        // loader reads it as its legacy `indexOffset` field. Both happen
        // to be 80 in this fixture so the loader's index lookup succeeds.
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(48, 4), indexOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(52, 4), 0);                 // kvByteLength
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(56, 4), dfdOffset);         // dataFormatDescriptorByteOffset
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(60, 4), (uint)dfdBytes.Length); // dataFormatDescriptorByteLength
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(64, 4), 0);                 // firstLevelOffset (legacy path: not used)
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(68, 4), 0);                 // sgdByteOffset
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(72, 4), 0);                 // sgdByteLength

        // Level index entry for 1 mip at offset 80 (24 bytes total):
        //   byteOffset u64, byteLength u64, uncompressedByteLength u64
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan((int)indexOffset, 8), dataOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan((int)(indexOffset + 8), 8), (ulong)comp.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan((int)(indexOffset + 16), 8), (ulong)uncomp.Length);

        Buffer.BlockCopy(dfdBytes, 0, result, (int)dfdOffset, dfdBytes.Length);
        Buffer.BlockCopy(comp, 0, result, (int)dataOffset, comp.Length);
        return result;
    }

    [Fact]
    public void Ktx2_DfdBasisuV210Writer_LoadsIntoRhiTexture()
    {
        // Reproduces the user's exact symptom: freshly-cooked .ktx2 from
        // `basisu -ktx2 -uastc` v2.10, which writes the DFD slot with the
        // vendor string "KTXwriter\0Basis Universal 2.10\0" rather than a
        // Khronos KHR_DF colorModel byte. Loader must accept via the
        // vendor-string detection branch in IsUastcDfd and route to
        // Astc4x4UnormBlock via the canonical RhiTexture.FromKhronosFormat
        // mapping.
        if (!ProbeDevice()) return;

        using var device = new RhiDevice();
        var path = Path.Combine(Path.GetTempPath(), $"dfd_basisu210_{Guid.NewGuid():N}.ktx2");
        File.WriteAllBytes(path, ForgeBasisuV210Ktx2(supercompressScheme: 2));
        try
        {
            using var tex = Ktx2Loader.Load(device, path);
            Assert.NotNull(tex);
            Assert.NotEqual(IntPtr.Zero, tex!.Handle);
        }
        finally { TryDeleteFile(path); }
    }

    [Fact]
    public void Ktx2_DfdNonUastcColorModel_ReturnsNull()
    {
        // colorModel values other than 166 (e.g. 163 = ETC1S, 0 = unrecognised)
        // must still be rejected — they require a Basis Universal transcoder
        // the runtime does not implement.
        if (!ProbeDevice()) return;

        using var device = new RhiDevice();
        var path = Path.Combine(Path.GetTempPath(), $"dfd_etc1s_{Guid.NewGuid():N}.ktx2");
        File.WriteAllBytes(path, ForgeDfdKtx2(supercompressScheme: 2, colorModel: 163));
        try
        {
            Assert.Null(Ktx2Loader.Load(device, path));
        }
        finally { TryDeleteFile(path); }
    }
}
