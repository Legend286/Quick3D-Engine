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
    /// The synthetic mip data is one solid-black BC1 block (8 bytes) — fine
    /// when the test exercises a rejection path that never reaches upload
    /// (e.g. vkFormat=0 → loader refuses before reading mip data).
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
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(48, 4), indexOffset);

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
        // vkFormat=0 (VK_FORMAT_UNDEFINED) with supercompression=2 hits the
        // loader's actionable diagnostic: it points the caller at
        // RhiTexture.FromKhronosVkFormat and tells them to re-cook with
        // `basisu -ktx2 -uastc` so the file lands on a real block-format id.
        // The Cook should not emit VK_FORMAT_UNDEFINED; if it does, the
        // loader must refuse the file rather than silently rendering
        // black/glitched texturing.
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
}
