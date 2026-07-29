// SPDX-License-Identifier: MIT
using System;
using System.Numerics;

namespace Engine.RHI;

public interface IGameLoop : IDisposable
{
    void Init(IntPtr deviceHandle, IntPtr swapchainHandle, IEntityStore world, bool enableImGui = true);
    void LoadScene(string contentRoot, string sceneName);
    void Update(InputState input);
    void RenderFrame(RhiTexture backBuffer, uint width, uint height);
    void RenderThumbnail(string contentRoot, string assetPath, string assetType, RhiTexture target, uint width = 256, uint height = 256, float orbitRadians = 0.0f, RhiFence? syncFence = null, ulong waitValue = 0, ulong signalValue = 0);
    void LoadModelPreview(string contentRoot, string modelPath);
    void LoadMaterialPreview(string contentRoot, string materialPath, bool usePathTracer = true);
    void RenderLoadedPreview(RhiTexture target, uint width = 256, uint height = 256, float orbitRadians = 0.0f, RhiFence? syncFence = null, ulong waitValue = 0, ulong signalValue = 0);
    void UpdateMaterialPreview(float[] albedo, float metallic, float roughness, float subsurface, float[] subsurfaceColor, float[] subsurfaceRadius, float clearcoat, float clearcoatRoughness, float[] topColor, float topMetallic, float topRoughness, uint topMaskType, float noiseScale = 10.0f, float noiseThresholdMin = 0.3f, float noiseThresholdMax = 0.7f, float[]? layer2Color = null, float layer2Metallic = 0.0f, float layer2Roughness = 1.0f, uint layer2MaskType = 0, float layer2NoiseScale = 10.0f, float layer2NoiseMin = 0.3f, float layer2NoiseMax = 0.7f);
    ulong AddPointLight(Vector3 position, Vector3 color, float intensity, float range, float sourceRadius, bool castShadows = true);
    ulong AddSpotLight(Vector3 position, Vector3 direction, Vector3 color, float intensity, float range, float innerCone, float outerCone, float sourceRadius, bool castShadows = true);
    void ReplaceSwapchain(RhiSwapchain swapchain);
    void SetSelectedEntity(ulong entityId);
    void ApplyMaterialToSubmesh(uint x, uint y, uint w, uint h, string materialPath);
    event Action<ulong>? OnEntityPicked;
}
