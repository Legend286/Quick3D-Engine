// SPDX-License-Identifier: MIT
using System;
using System.IO;
using Engine.RHI;
using StbImageSharp;
using Engine.CBindings;

namespace Engine.Assets;

public static class TextureLoader
{
    /// <summary>
    /// Loads a texture from disk. Detects the file format by extension:
    /// - .ktx2 dispatches to Ktx2Loader (block-compressed GPU formats such as
    ///   BC1/3/5/7, ETC2 RGB8, ASTC 4x4)
    /// - all other extensions fall through to StbImageSharp (PNG/JPEG/BMP/TGA/HDR)
    /// uncompressed uploads go through RhiTexture.Upload.
    /// </summary>
    public static RhiTexture? LoadTexture(RhiDevice device, string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"Texture not found: {path}");
            return null;
        }

        if (IsKtx2(path))
        {
            return Ktx2Loader.Load(device, path);
        }

        try
        {
            using var stream = File.OpenRead(path);
            var result = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

            var tex = RhiTexture.Create2D(device, (uint)result.Width, (uint)result.Height, RhiNative.TextureFormat.Rgba8Unorm);

            unsafe
            {
                fixed (byte* p = result.Data)
                {
                    tex.Upload((IntPtr)p, (ulong)result.Data.Length, (uint)(result.Width * 4));
                }
            }

            return tex;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load texture {path}: {ex.Message}");
            return null;
        }
    }

    private static bool IsKtx2(string path)
    {
        if (path.Length < 5) return false;
        ReadOnlySpan<char> ext = path.AsSpan(path.Length - 5);
        if (ext[0] != '.') return false;
        char c1 = ext[1], c2 = ext[2], c3 = ext[3], c4 = ext[4];
        return (c1 == 'k' || c1 == 'K')
            && (c2 == 't' || c2 == 'T')
            && (c3 == 'x' || c3 == 'X')
            && c4 == '2';
    }
}
