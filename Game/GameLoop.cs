// SPDX-License-Identifier: MIT
using System;
using Engine.RHI;

namespace Engine.Game;

public sealed class GameLoop : IGameLoop
{
    private RhiDevice? _device;
    private RhiSwapchain? _swap;
    private IEntityStore? _world;
    private Renderer? _renderer;

    public void Init(IntPtr deviceHandle, IntPtr swapchainHandle, IEntityStore world)
    {
        _device = new RhiDevice(deviceHandle, ownsHandle: false);
        _swap = new RhiSwapchain(_device, swapchainHandle, ownsHandle: false);
        _world = world;
        _renderer = new Renderer(_device, _swap, _world);
        // Seed the world from game code so hot-reload picks up geometry/color changes.
        SeedWorld(world);
    }

    private static void SeedWorld(IEntityStore world)
    {
        // Find or create the triangle entity.
        ulong ent = 0;
        for (ulong id = 1; id < 1024; ++id)
        {
            if (world.TryGet<TriangleComponent>(id, out _)) { ent = id; break; }
        }
        if (ent == 0) ent = world.CreateEntity();

        // ---- Edit colors here and hit Hot Reload to see changes instantly ----
        world.Set(ent, TriangleComponent.Create(
            new float[] {  0.0f,  0.6f, 0.0f,
                          -0.6f, -0.4f, 0.0f,
                           0.6f, -0.4f, 0.0f },
            new float[] { 1, 0, 0,   0, 1, 0,   0, 1, 1 }  // R / G / cyan
        ));
    }

    public void LoadScene(string contentRoot, string sceneName)
    {
        _renderer?.LoadScene(contentRoot, sceneName);
    }

    public void RenderFrame(RhiTexture backBuffer, uint width, uint height)
    {
        _renderer?.RenderFrame(backBuffer, width, height);
    }

    public void Dispose()
    {
        _renderer?.Dispose();
        _renderer = null;
        _swap?.Dispose();
        _swap = null;
        _device?.Dispose();
        _device = null;
    }
}
