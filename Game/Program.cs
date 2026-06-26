// SPDX-License-Identifier: MIT
// Phase 2 hello-triangle entry point. Boots the renderer against an external
// NSWindow handle (provided by the Avalonia viewport panel in the Editor;
// the Game exe can also be launched standalone with a CLI window pointer).

using System;
using System.Globalization;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.Scene;
using Engine.Game;

internal static class Program
{
    public static int Main(string[] args)
    {
        string contentRoot = ResolveContentRoot(args);
        IntPtr window = ResolveNativeWindow(args);
        uint width = 1280;
        uint height = 720;
        if (window == IntPtr.Zero)
        {
            Console.Error.WriteLine("Usage: Engine.Game --content <path> --window <hex-ptr> [--width n] [--height n]");
            return 2;
        }
        for (int i = 0; i + 1 < args.Length; ++i)
        {
            if (args[i] == "--width" && uint.TryParse(args[i + 1], out var w)) width = w;
            if (args[i] == "--height" && uint.TryParse(args[i + 1], out var h)) height = h;
        }

        using var device = new RhiDevice();
        using var swap = device.CreateSwapchain(window, width, height);
        using var world = new EcsWorld();  // now IDisposable

        // Seed: a single triangle entity at the origin. Phase 3 reads the
        // entity table from Content/scenes/hello.scene.json.
        SeedTriangleEntity(world);

        var renderer = new Renderer(device, swap, world);
        renderer.LoadScene(contentRoot, "hello");

        if (swap.TryAcquireNextImage(out RhiTexture? image))
        {
            using (image)
            {
                renderer.RenderFrame(image, width, height);
                swap.Present();
            }
        }
        return 0;
    }

    private static void SeedTriangleEntity(EcsWorld world)
    {
        ulong ent = world.CreateEntity();
        world.Set(ent, new TriangleComponent
        {
            Positions = new float[]
            {
                 0.0f,  0.6f, 0.0f,
                -0.6f, -0.4f, 0.0f,
                 0.6f, -0.4f, 0.0f,
            },
            Colors = new float[]
            {
                1,0,0, 0,1,0, 0,0,1,
            },
        });
    }

    private static string ResolveContentRoot(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; ++i)
            if (args[i] == "--content") return args[i + 1];
        return "Content";
    }

    private static IntPtr ResolveNativeWindow(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; ++i)
        {
            if (args[i] == "--window")
            {
                if (IntPtr.TryParse(args[i + 1], NumberStyles.HexNumber,
                                     CultureInfo.InvariantCulture, out var p))
                    return p;
            }
        }
        return IntPtr.Zero;
    }
}
