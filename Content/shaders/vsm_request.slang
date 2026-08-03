// SPDX-License-Identifier: MIT
#include "scene_data.slang"

[push_constant]
ConstantBuffer<ScenePushData> push;

[[vk::binding(0, 0)]] Texture2D<float> sceneDepth : register(t0);
[[vk::binding(1, 0)]] RWStructuredBuffer<uint> PageRequests : register(u1);

struct VsmRequestConstants {
    float4x4 lightViewProj;
    uint virtualPagesX;
    uint virtualPagesY;
    uint2 screenDimensions;
};

[[vk::binding(2, 0)]] ConstantBuffer<VsmRequestConstants> vsmConstants : register(b2);
[[vk::binding(3, 0)]] ConstantBuffer<CameraData> cameraData : register(b3);

[numthreads(8, 8, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    if (any(dispatchThreadId.xy >= vsmConstants.screenDimensions))
        return;

    float depth = sceneDepth.Load(int3(dispatchThreadId.xy, 0));
    
    // Depth is typically 0.0 for far plane in reversed-Z, or 1.0 for far plane in standard Z.
    // Assuming reversed-Z based on modern engine conventions (0.0 is background/sky)
    if (depth <= 0.0)
        return;

    // Convert pixel to NDC
    float2 uv = (float2(dispatchThreadId.xy) + 0.5) / float2(vsmConstants.screenDimensions);
    float x = uv.x * 2.0 - 1.0;
    float y = uv.y * -2.0 + 1.0; // Y is up in NDC
    
    float4 ndc = float4(x, y, depth, 1.0);
    float4 worldPos = mul(ndc, cameraData.invViewProj);
    worldPos /= worldPos.w;

    // Project world space into Light Clip Space
    float4 lightClipPos = mul(worldPos, vsmConstants.lightViewProj);
    
    // Check if behind light
    if (lightClipPos.w <= 0.0) return;

    lightClipPos /= lightClipPos.w;

    // Convert Light Clip Space to Virtual UV (0 to 1)
    float2 virtualUV;
    virtualUV.x = lightClipPos.x * 0.5 + 0.5;
    virtualUV.y = lightClipPos.y * -0.5 + 0.5; // Vulkan/Metal UV orientation

    // Check bounds
    if (any(virtualUV < 0.0) || any(virtualUV > 1.0))
        return;

    // Determine Virtual Page Coordinates
    uint pageX = (uint)(virtualUV.x * float(vsmConstants.virtualPagesX));
    uint pageY = (uint)(virtualUV.y * float(vsmConstants.virtualPagesY));

    // Clamp just in case
    pageX = min(pageX, vsmConstants.virtualPagesX - 1);
    pageY = min(pageY, vsmConstants.virtualPagesY - 1);

    // Calculate flat page index
    uint pageIndex = pageY * vsmConstants.virtualPagesX + pageX;

    // Mark the page as requested
    PageRequests[pageIndex] = 1;
}
