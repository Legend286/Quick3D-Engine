// SPDX-License-Identifier: MIT
using System;
using Engine.RHI;
using static Engine.CBindings.Log;

namespace Engine.Game;

public sealed class GameLoop : IGameLoop
{
    private RhiDevice? _device;
    private RhiSwapchain? _swap;
    private IEntityStore? _world;
    private Renderer? _renderer;

    public void Init(IntPtr deviceHandle, IntPtr swapchainHandle, IEntityStore world)
    {
        Info("[GameLoop] Initializing...", "Game");
        _device = new RhiDevice(deviceHandle, ownsHandle: false);
        _swap = new RhiSwapchain(_device, swapchainHandle, ownsHandle: false);
        _world = world;
        _world?.Clear();
        _renderer = new Renderer(_device, _swap, _world);
        Info("[GameLoop] Initialized successfully", "Game");
    }

    private static void SeedWorld(IEntityStore world)
    {
        // Find or create the mesh entity.
        ulong ent = 0;
        for (ulong id = 1; id < 1024; ++id)
        {
            if (world.TryGet<MeshComponent>(id, out _)) { ent = id; break; }
        }
        if (ent == 0) ent = world.CreateEntity();

        Debug($"[GameLoop] Seeding mesh entity {ent}", "Game");

        // ---- Edit colors here and hit Hot Reload to see changes instantly ----
        world.Set(ent, MeshComponent.Create(
            new float[] {  
                // Top Triangle
                 0.0f,  0.6f, 0.0f,
                -0.6f, -0.4f, 0.0f,
                 0.6f, -0.4f, 0.0f,
                // Bottom Triangle
                 0.6f, -0.4f, 0.0f,
                -0.6f, -0.4f, 0.0f,
                 0.0f, -1.4f, 0.0f
            },
            new float[] {
                1, 0, 0,   // R
                0, 1, 0,   // G
                0, 0, 1,   // B
                
                0, 0, 1,   // B
                0, 1, 0,   // G
                1, 1, 1    // White
            }
        ));
    }

    public void LoadScene(string contentRoot, string sceneName)
    {
        _renderer?.LoadScene(contentRoot, sceneName);
        // Re-seed AFTER scene load so game-code edits always override scene defaults.
        // This is what makes hot-reload vertex/color edits take effect:
        // the scene JSON provides fallback geometry, but SeedWorld has final say.
        if (_world is not null)
            SeedWorld(_world);
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
