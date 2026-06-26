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
