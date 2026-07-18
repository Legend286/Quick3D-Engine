// SPDX-License-Identifier: MIT
using System;

namespace Engine.RHI;

public interface IGameLoop : IDisposable
{
    void Init(IntPtr deviceHandle, IntPtr swapchainHandle, IEntityStore world);
    void LoadScene(string contentRoot, string sceneName);
    void Update(InputState input);
    void RenderFrame(RhiTexture backBuffer, uint width, uint height);
    void RenderThumbnail(string contentRoot, string assetPath, string assetType, RhiTexture target);
    void SetSelectedEntity(ulong entityId);
    event Action<ulong>? OnEntityPicked;
}
