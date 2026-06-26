// SPDX-License-Identifier: MIT
using System;

namespace Engine.RHI;

public interface IGameLoop : IDisposable
{
    void Init(IntPtr deviceHandle, IntPtr swapchainHandle, IEntityStore world);
    void LoadScene(string contentRoot, string sceneName);
    void RenderFrame(RhiTexture backBuffer, uint width, uint height);
}
