// SPDX-License-Identifier: MIT
using System;

namespace Engine.RHI;

public interface IGameLoop : IDisposable
{
    void Init(IntPtr deviceHandle, IntPtr swapchainHandle, IEntityStore world, bool enableImGui = true);
    void LoadScene(string contentRoot, string sceneName);
    void Update(InputState input);
    void RenderFrame(RhiTexture backBuffer, uint width, uint height);
    void RenderThumbnail(string contentRoot, string assetPath, string assetType, RhiTexture target);
    void LoadMaterialPreview(string contentRoot, string materialPath);
    void UpdateMaterialPreview(float[] albedo, float metallic, float roughness, float subsurface, float[] subsurfaceColor, float[] subsurfaceRadius, float clearcoat, float clearcoatRoughness, float[] topColor, float topMetallic, float topRoughness, uint topMaskType);
    void SetSelectedEntity(ulong entityId);
    void ApplyMaterialToSubmesh(uint x, uint y, uint w, uint h, string materialPath);
    event Action<ulong>? OnEntityPicked;
}

