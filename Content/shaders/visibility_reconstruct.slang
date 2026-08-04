// SPDX-License-Identifier: MIT
#include "scene_data.slang"
#include "visibility_debug_common.slang"

[push_constant]
ConstantBuffer<ScenePushData> push;

[[vk::binding(4, 0)]]
Texture2D<uint2> visibilityIdentifiers : register(t4);
[[vk::binding(5, 0)]]
Texture2D<float2> visibilityBarycentrics : register(t5);
[[vk::binding(6, 0)]]
Texture2D<float> sceneDepth : register(t6);
[[vk::binding(0, 0)]]
RWTexture2D<float4> reconstructionOutput : register(u0);

struct BindlessHeap
{
    Texture2D textures[4096];
};
[[vk::binding(1, 0)]] ParameterBlock<BindlessHeap> bindless;
[[vk::binding(2, 0)]] SamplerState textureSampler;

bool TryLoadNeighborUv(
    int2 pixel,
    int2 offset,
    uint2 dimensions,
    uint2 identifiers,
    float2 uv0,
    float2 uv1,
    float2 uv2,
    out float2 uv)
{
    int2 neighbor = pixel + offset;
    if (any(neighbor < 0) || any(neighbor >= int2(dimensions)))
    {
        uv = 0.0;
        return false;
    }
    uint2 neighborIdentifiers =
        visibilityIdentifiers.Load(int3(neighbor, 0));
    if (any(neighborIdentifiers != identifiers))
    {
        uv = 0.0;
        return false;
    }
    float2 neighborXY =
        visibilityBarycentrics.Load(int3(neighbor, 0));
    float3 neighborBarycentrics = max(
        float3(
            neighborXY,
            1.0 - neighborXY.x - neighborXY.y),
        0.0);
    neighborBarycentrics /= max(
        neighborBarycentrics.x +
            neighborBarycentrics.y +
            neighborBarycentrics.z,
        1.0e-6);
    uv =
        neighborBarycentrics.x * uv0 +
        neighborBarycentrics.y * uv1 +
        neighborBarycentrics.z * uv2;
    return true;
}

float2 ReconstructUvGradient(
    int2 pixel,
    int2 axis,
    uint2 dimensions,
    uint2 identifiers,
    float2 currentUv,
    float2 uv0,
    float2 uv1,
    float2 uv2)
{
    float2 neighborUv;
    if (TryLoadNeighborUv(
            pixel,
            axis,
            dimensions,
            identifiers,
            uv0,
            uv1,
            uv2,
            neighborUv))
    {
        return neighborUv - currentUv;
    }
    if (TryLoadNeighborUv(
            pixel,
            -axis,
            dimensions,
            identifiers,
            uv0,
            uv1,
            uv2,
            neighborUv))
    {
        return currentUv - neighborUv;
    }
    return 0.0;
}

[shader("compute")]
[numthreads(8, 8, 1)]
void computeMain(uint3 threadIndex : SV_DispatchThreadID)
{
    uint2 dimensions = uint2(push.resolution.xy);
    uint2 pixel = threadIndex.xy;
    if (any(pixel >= dimensions))
        return;

    float depth = sceneDepth.Load(int3(pixel, 0));
    if (depth >= 0.999999)
    {
        reconstructionOutput[pixel] = float4(
            0.012,
            0.015,
            0.022,
            1.0);
        return;
    }

    uint2 identifiers = visibilityIdentifiers.Load(int3(pixel, 0));
    PartData part = push.parts[identifiers.x];
    uint triangleOffset = identifiers.y * 3u;
    if (triangleOffset + 2u >= part.indexCount)
    {
        reconstructionOutput[pixel] = float4(
            1.0,
            0.0,
            1.0,
            1.0);
        return;
    }

    float2 barycentricXY =
        visibilityBarycentrics.Load(int3(pixel, 0));
    float3 barycentrics = max(
        float3(
            barycentricXY,
            1.0 - barycentricXY.x - barycentricXY.y),
        0.0);
    barycentrics /= max(
        barycentrics.x + barycentrics.y + barycentrics.z,
        1.0e-6);

    uint index0 = LoadPartVertexIndex(part, triangleOffset);
    uint index1 = LoadPartVertexIndex(part, triangleOffset + 1u);
    uint index2 = LoadPartVertexIndex(part, triangleOffset + 2u);
    InstanceData instance = push.instances[part.instanceIdx];
    MaterialData material = push.materials[part.materialIdx];
    uint debugView = push.debugFlags & 0xffu;
    bool needsUv = debugView == 16u;
    bool needsTangent = debugView == 19u;

    float3 position0 = LoadVertexPosition(part, index0);
    float3 position1 = LoadVertexPosition(part, index1);
    float3 position2 = LoadVertexPosition(part, index2);
    float3 normal0 = LoadVertexNormal(part, index0);
    float3 normal1 = LoadVertexNormal(part, index1);
    float3 normal2 = LoadVertexNormal(part, index2);
    float3 localPosition =
        barycentrics.x * position0 +
        barycentrics.y * position1 +
        barycentrics.z * position2 +
        part.localOffset.xyz;
    float3 worldPosition = mul(
        instance.modelMatrix,
        float4(localPosition, 1.0)).xyz;
    float3 worldNormal0 = normalize(mul(
        (float3x3)instance.modelMatrix, normal0));
    float3 worldNormal1 = normalize(mul(
        (float3x3)instance.modelMatrix, normal1));
    float3 worldNormal2 = normalize(mul(
        (float3x3)instance.modelMatrix, normal2));
    float3 worldNormal = normalize(
        barycentrics.x * worldNormal0 +
        barycentrics.y * worldNormal1 +
        barycentrics.z * worldNormal2);

    float3 worldTangent = 0.0;
    float tangentSign = 1.0;
    float3 worldBitangent = 0.0;
    float2 uv0 = 0.0;
    float2 uv1 = 0.0;
    float2 uv2 = 0.0;
    if (needsTangent)
    {
        float4 tangent0 = LoadVertexTangent(part, index0);
        float4 tangent1 = LoadVertexTangent(part, index1);
        float4 tangent2 = LoadVertexTangent(part, index2);
        float3 worldTangent0 = normalize(mul(
            (float3x3)instance.modelMatrix, tangent0.xyz));
        float3 worldTangent1 = normalize(mul(
            (float3x3)instance.modelMatrix, tangent1.xyz));
        float3 worldTangent2 = normalize(mul(
            (float3x3)instance.modelMatrix, tangent2.xyz));
        worldTangent = normalize(
            barycentrics.x * worldTangent0 +
            barycentrics.y * worldTangent1 +
            barycentrics.z * worldTangent2);
        tangentSign =
            barycentrics.x * tangent0.w +
            barycentrics.y * tangent1.w +
            barycentrics.z * tangent2.w;
        if (!(dot(worldTangent, worldTangent) > 0.001))
        {
            float3 up = abs(worldNormal.y) < 0.999
                ? float3(0.0, 1.0, 0.0)
                : float3(1.0, 0.0, 0.0);
            worldTangent = normalize(cross(up, worldNormal));
        }
        worldBitangent =
            cross(worldNormal, worldTangent) * tangentSign;
        if (!(dot(worldBitangent, worldBitangent) > 0.001))
            worldBitangent = cross(worldNormal, worldTangent);
        worldBitangent = normalize(worldBitangent);
        worldTangent = normalize(cross(worldBitangent, worldNormal));
    }
    if (needsUv)
    {
        uv0 = LoadVertexUv(part, index0);
        uv1 = LoadVertexUv(part, index1);
        uv2 = LoadVertexUv(part, index2);
    }
    float2 uv = needsUv
        ? barycentrics.x * uv0 +
            barycentrics.y * uv1 +
            barycentrics.z * uv2
        : 0.0;
    float3 color = float3(1.0, 0.0, 1.0);
    if (debugView == 14u)
        color = worldPosition;
    else if (debugView == 15u)
        color = worldNormal;
    else if (debugView == 16u)
        color = float3(uv, 0.0);
    else if (debugView == 17u)
        color = VisibilityIdentifierColor(
            uint2(part.materialIdx, 0x4d41544cu));
    else if (debugView == 18u)
        color = VisibilityIdentifierColor(
            uint2(
                part.instanceIdx,
                instance.entityIdLow ^ instance.entityIdHigh));
    else if (debugView == 19u)
        color = worldTangent;
    reconstructionOutput[pixel] = float4(color, 1.0);
}
