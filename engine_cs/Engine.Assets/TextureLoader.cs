// SPDX-License-Identifier: MIT
using System;
using System.IO;
using Engine.RHI;
using StbImageSharp;
using Engine.CBindings;

namespace Engine.Assets;

public static class TextureLoader
{
    public static RhiTexture? LoadTexture(RhiDevice device, string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"Texture not found: {path}");
            return null;
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
}
