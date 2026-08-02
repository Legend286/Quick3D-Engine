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
[[vk::binding(7, 0)]]
RWTexture2D<float4> reconstructionOutput : register(u7);

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
    Vertex vertex0 = part.vertices[index0];
    Vertex vertex1 = part.vertices[index1];
    Vertex vertex2 = part.vertices[index2];
    InstanceData instance = push.instances[part.instanceIdx];

    float3 localPosition =
        barycentrics.x * float3(vertex0.px, vertex0.py, vertex0.pz) +
        barycentrics.y * float3(vertex1.px, vertex1.py, vertex1.pz) +
        barycentrics.z * float3(vertex2.px, vertex2.py, vertex2.pz) +
        part.localOffset.xyz;
    float3 worldPosition = mul(
        instance.modelMatrix,
        float4(localPosition, 1.0)).xyz;
    float3 worldNormal0 = normalize(mul(
        (float3x3)instance.modelMatrix,
        float3(vertex0.nx, vertex0.ny, vertex0.nz)));
    float3 worldNormal1 = normalize(mul(
        (float3x3)instance.modelMatrix,
        float3(vertex1.nx, vertex1.ny, vertex1.nz)));
    float3 worldNormal2 = normalize(mul(
        (float3x3)instance.modelMatrix,
        float3(vertex2.nx, vertex2.ny, vertex2.nz)));
    float3 worldNormal = normalize(
        barycentrics.x * worldNormal0 +
        barycentrics.y * worldNormal1 +
        barycentrics.z * worldNormal2);
    float3 worldTangent0 = normalize(mul(
        (float3x3)instance.modelMatrix,
        float3(vertex0.tx, vertex0.ty, vertex0.tz)));
    float3 worldTangent1 = normalize(mul(
        (float3x3)instance.modelMatrix,
        float3(vertex1.tx, vertex1.ty, vertex1.tz)));
    float3 worldTangent2 = normalize(mul(
        (float3x3)instance.modelMatrix,
        float3(vertex2.tx, vertex2.ty, vertex2.tz)));
    float3 worldTangent = normalize(
        barycentrics.x * worldTangent0 +
        barycentrics.y * worldTangent1 +
        barycentrics.z * worldTangent2);
    float tangentSign =
        barycentrics.x * vertex0.tw +
        barycentrics.y * vertex1.tw +
        barycentrics.z * vertex2.tw;
    if (!(dot(worldTangent, worldTangent) > 0.001))
    {
        float3 up = abs(worldNormal.y) < 0.999
            ? float3(0.0, 1.0, 0.0)
            : float3(1.0, 0.0, 0.0);
        worldTangent = normalize(cross(up, worldNormal));
    }
    float3 worldBitangent =
        cross(worldNormal, worldTangent) * tangentSign;
    if (!(dot(worldBitangent, worldBitangent) > 0.001))
        worldBitangent = cross(worldNormal, worldTangent);
    worldBitangent = normalize(worldBitangent);
    worldTangent = normalize(cross(worldBitangent, worldNormal));
    float2 uv0 = float2(vertex0.u, vertex0.v);
    float2 uv1 = float2(vertex1.u, vertex1.v);
    float2 uv2 = float2(vertex2.u, vertex2.v);
    float2 uv =
        barycentrics.x * uv0 +
        barycentrics.y * uv1 +
        barycentrics.z * uv2;

    uint debugView = push.debugFlags & 0xffu;
    MaterialData material = push.materials[part.materialIdx];
    if ((debugView == 15u || debugView == 19u) &&
        material.normalTexIndex != 0xffffffffu)
    {
        float2 uvDx = ReconstructUvGradient(
            int2(pixel),
            int2(1, 0),
            dimensions,
            identifiers,
            uv,
            uv0,
            uv1,
            uv2);
        float2 uvDy = ReconstructUvGradient(
            int2(pixel),
            int2(0, 1),
            dimensions,
            identifiers,
            uv,
            uv0,
            uv1,
            uv2);
        float3 tangentNormal =
            bindless.textures[material.normalTexIndex]
                .SampleGrad(textureSampler, uv, uvDx, uvDy).rgb * 2.0 - 1.0;
        worldNormal = normalize(mul(
            tangentNormal,
            float3x3(
                worldTangent,
                worldBitangent,
                worldNormal)));
        worldBitangent =
            cross(worldNormal, worldTangent) * tangentSign;
        if (!(dot(worldBitangent, worldBitangent) > 0.001))
            worldBitangent = cross(worldNormal, worldTangent);
        worldBitangent = normalize(worldBitangent);
        worldTangent = normalize(cross(worldBitangent, worldNormal));
    }

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
