// SPDX-License-Identifier: MIT
#define VISIBILITY_COMPUTE 1
#include "pbr.slang"
#include "nishita_sky.slang"

[[vk::binding(4, 0)]]
Texture2D<uint2> visibilityIdentifiers : register(t4);
[[vk::binding(5, 0)]]
Texture2D<float2> visibilityBarycentrics : register(t5);
[[vk::binding(6, 0)]]
Texture2D<float> sceneDepth : register(t6);
[[vk::binding(7, 0)]]
RWTexture2D<float4> visibilityShadingOutput : register(u7);

static const uint kTileWidth = 8u;
static const uint kTileThreadCount = 64u;
static const uint kTileLightCapacity = 1024u;
static const uint kInvalidTileLight = 0xffffffffu;

groupshared uint g_tileDepthSliceMask;
groupshared uint g_tileHasGeometry;
groupshared uint g_tileLightCount;
groupshared uint g_tileLightHash[kTileLightCapacity];
groupshared uint g_tileLights[kTileLightCapacity];

uint VisibilityTileLightCount()
{
    return min(g_tileLightCount, kTileLightCapacity);
}

uint VisibilityTileLightIndex(uint tileIndex)
{
    return g_tileLights[tileIndex];
}

void InsertTileLight(uint lightIndex)
{
    uint hashIndex =
        (lightIndex * 0x9e3779b9u) & (kTileLightCapacity - 1u);
    for (uint attempt = 0u;
         attempt < kTileLightCapacity;
         ++attempt)
    {
        uint previous;
        InterlockedCompareExchange(
            g_tileLightHash[hashIndex],
            kInvalidTileLight,
            lightIndex,
            previous);
        if (previous == kInvalidTileLight || previous == lightIndex)
            return;
        hashIndex = (hashIndex + 1u) & (kTileLightCapacity - 1u);
    }
}

float3 DecodeBarycentrics(float2 barycentricXY)
{
    float3 barycentrics = max(
        float3(
            barycentricXY,
            1.0 - barycentricXY.x - barycentricXY.y),
        0.0);
    return barycentrics / max(
        barycentrics.x + barycentrics.y + barycentrics.z,
        1.0e-6);
}

bool TryLoadNeighborBarycentrics(
    int2 pixel,
    int2 offset,
    uint2 dimensions,
    uint2 identifiers,
    out float3 barycentrics)
{
    int2 neighbor = pixel + offset;
    if (any(neighbor < 0) || any(neighbor >= int2(dimensions)) ||
        any(visibilityIdentifiers.Load(int3(neighbor, 0)) != identifiers))
    {
        barycentrics = 0.0;
        return false;
    }
    barycentrics = DecodeBarycentrics(
        visibilityBarycentrics.Load(int3(neighbor, 0)));
    return true;
}

float2 ReconstructUvGradient(
    int2 pixel,
    int2 axis,
    uint2 dimensions,
    uint2 identifiers,
    float3 barycentrics,
    float2 uv0,
    float2 uv1,
    float2 uv2)
{
    float2 currentUv =
        barycentrics.x * uv0 +
        barycentrics.y * uv1 +
        barycentrics.z * uv2;
    float3 neighborBarycentrics;
    if (TryLoadNeighborBarycentrics(
            pixel,
            axis,
            dimensions,
            identifiers,
            neighborBarycentrics))
    {
        return
            neighborBarycentrics.x * uv0 +
            neighborBarycentrics.y * uv1 +
            neighborBarycentrics.z * uv2 - currentUv;
    }
    if (TryLoadNeighborBarycentrics(
            pixel,
            -axis,
            dimensions,
            identifiers,
            neighborBarycentrics))
    {
        return currentUv - (
            neighborBarycentrics.x * uv0 +
            neighborBarycentrics.y * uv1 +
            neighborBarycentrics.z * uv2);
    }
    return 0.0;
}

float3 ReconstructNormalGradient(
    int2 pixel,
    int2 axis,
    uint2 dimensions,
    uint2 identifiers,
    float3 barycentrics,
    float3 normal0,
    float3 normal1,
    float3 normal2)
{
    float3 currentNormal =
        barycentrics.x * normal0 +
        barycentrics.y * normal1 +
        barycentrics.z * normal2;
    float3 neighborBarycentrics;
    if (TryLoadNeighborBarycentrics(
            pixel,
            axis,
            dimensions,
            identifiers,
            neighborBarycentrics))
    {
        return
            neighborBarycentrics.x * normal0 +
            neighborBarycentrics.y * normal1 +
            neighborBarycentrics.z * normal2 - currentNormal;
    }
    if (TryLoadNeighborBarycentrics(
            pixel,
            -axis,
            dimensions,
            identifiers,
            neighborBarycentrics))
    {
        return currentNormal - (
            neighborBarycentrics.x * normal0 +
            neighborBarycentrics.y * normal1 +
            neighborBarycentrics.z * normal2);
    }
    return 0.0;
}

float3 EvaluateVisibilitySky(uint2 pixel, uint2 dimensions)
{
    if ((push.debugFlags & 0x80000000u) == 0u)
        return float3(0.0);
    float2 uv = (float2(pixel) + 0.5) / float2(dimensions);
    float2 ndc = float2(
        uv.x * 2.0 - 1.0,
        1.0 - uv.y * 2.0);
    float4 target = mul(
        push.camera->invViewProj,
        float4(ndc, 1.0, 1.0));
    float3 worldPosition = target.xyz / target.w;
    float3 viewDirection = normalize(
        worldPosition - push.camera->cameraPosition.xyz);
    float3 color = GetSkyRadiance(
        viewDirection,
        push.sky.sunDirAndRadius.xyz,
        push.sky);
    color *= push.camera->cameraPosition.w;
    color = clamp(
        (color * (2.51 * color + 0.03)) /
            (color * (2.43 * color + 0.59) + 0.14),
        0.0,
        1.0);
    return pow(color, float3(1.0 / 2.2));
}

[shader("compute")]
[numthreads(8, 8, 1)]
void computeMain(
    uint3 groupIndex : SV_GroupID,
    uint3 groupThreadIndex : SV_GroupThreadID,
    uint3 dispatchThreadIndex : SV_DispatchThreadID)
{
    uint lane = groupThreadIndex.y * kTileWidth + groupThreadIndex.x;
    uint2 dimensions = uint2(push.resolution.xy);
    uint2 pixel = dispatchThreadIndex.xy;
    bool inBounds = all(pixel < dimensions);
    float depth = inBounds
        ? sceneDepth.Load(int3(pixel, 0))
        : 1.0;
    bool covered = inBounds && depth < 0.999999;

    if (lane == 0u)
    {
        g_tileDepthSliceMask = 0u;
        g_tileHasGeometry = 0u;
        g_tileLightCount = 0u;
    }
    GroupMemoryBarrierWithGroupSync();

    bool useClusters =
        push.clusterGrid.w > 0.0 && push.clusterParams.y > 0.0;
    uint gridZ = max((uint)push.clusterGrid.z, 1u);
    if (covered)
    {
        InterlockedOr(g_tileHasGeometry, 1u);
        if (useClusters)
        {
            uint depthSlice = min(
                (uint)(depth * float(gridZ)),
                gridZ - 1u);
            InterlockedOr(
                g_tileDepthSliceMask,
                1u << depthSlice);
        }
    }
    GroupMemoryBarrierWithGroupSync();

    if (g_tileHasGeometry == 0u)
    {
        if (inBounds)
        {
            visibilityShadingOutput[pixel] = float4(
                EvaluateVisibilitySky(pixel, dimensions),
                1.0);
        }
        return;
    }

    if (!useClusters)
    {
        uint directLightCount = min(
            push.lightCount,
            kTileLightCapacity);
        for (uint lightIndex = lane;
             lightIndex < directLightCount;
             lightIndex += kTileThreadCount)
        {
            g_tileLights[lightIndex] = lightIndex;
        }
        if (lane == 0u)
            g_tileLightCount = directLightCount;
    }
    else
    {
        for (uint hashIndex = lane;
             hashIndex < kTileLightCapacity;
             hashIndex += kTileThreadCount)
        {
            g_tileLightHash[hashIndex] = kInvalidTileLight;
        }
        GroupMemoryBarrierWithGroupSync();

        if (lane < gridZ &&
            (g_tileDepthSliceMask & (1u << lane)) != 0u)
        {
            uint gridX = (uint)push.clusterGrid.x;
            uint gridY = (uint)push.clusterGrid.y;
            float clusterTileSize = push.clusterParams.x;
            uint2 tileOrigin = groupIndex.xy * kTileWidth;
            uint clusterX = min(
                (uint)(tileOrigin.x / clusterTileSize),
                gridX - 1u);
            uint clusterY = min(
                (uint)(tileOrigin.y / clusterTileSize),
                gridY - 1u);
            uint clusterIndex =
                lane * gridX * gridY + clusterY * gridX + clusterX;
            ClusterRecord record = push.clusterRecords[clusterIndex];
            for (uint lightOffset = 0u;
                 lightOffset < record.count;
                 ++lightOffset)
            {
                InsertTileLight(
                    push.clusterLightIndices[
                        record.offset + lightOffset]);
            }
        }
        GroupMemoryBarrierWithGroupSync();

        for (uint hashIndex = lane;
             hashIndex < kTileLightCapacity;
             hashIndex += kTileThreadCount)
        {
            uint lightIndex = g_tileLightHash[hashIndex];
            if (lightIndex == kInvalidTileLight)
                continue;
            uint outputIndex;
            InterlockedAdd(g_tileLightCount, 1u, outputIndex);
            g_tileLights[outputIndex] = lightIndex;
        }
    }
    GroupMemoryBarrierWithGroupSync();

    if (!inBounds)
        return;
    if (!covered)
    {
        visibilityShadingOutput[pixel] = float4(
            EvaluateVisibilitySky(pixel, dimensions),
            1.0);
        return;
    }

    uint2 identifiers = visibilityIdentifiers.Load(int3(pixel, 0));
    PartData part = push.parts[identifiers.x];
    uint triangleOffset = identifiers.y * 3u;
    if (triangleOffset + 2u >= part.indexCount)
    {
        visibilityShadingOutput[pixel] = float4(1.0, 0.0, 1.0, 1.0);
        return;
    }

    float3 barycentrics = DecodeBarycentrics(
        visibilityBarycentrics.Load(int3(pixel, 0)));
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
    float3 worldNormal0 = normalize(mul(
        (float3x3)instance.modelMatrix,
        float3(vertex0.nx, vertex0.ny, vertex0.nz)));
    float3 worldNormal1 = normalize(mul(
        (float3x3)instance.modelMatrix,
        float3(vertex1.nx, vertex1.ny, vertex1.nz)));
    float3 worldNormal2 = normalize(mul(
        (float3x3)instance.modelMatrix,
        float3(vertex2.nx, vertex2.ny, vertex2.nz)));
    float3 worldTangent0 = normalize(mul(
        (float3x3)instance.modelMatrix,
        float3(vertex0.tx, vertex0.ty, vertex0.tz)));
    float3 worldTangent1 = normalize(mul(
        (float3x3)instance.modelMatrix,
        float3(vertex1.tx, vertex1.ty, vertex1.tz)));
    float3 worldTangent2 = normalize(mul(
        (float3x3)instance.modelMatrix,
        float3(vertex2.tx, vertex2.ty, vertex2.tz)));
    float2 uv0 = float2(vertex0.u, vertex0.v);
    float2 uv1 = float2(vertex1.u, vertex1.v);
    float2 uv2 = float2(vertex2.u, vertex2.v);

    VertexOutput input;
    input.position = float4(
        float2(pixel) + 0.5,
        depth,
        1.0);
    input.worldPos = mul(
        instance.modelMatrix,
        float4(localPosition, 1.0)).xyz;
    input.normal =
        barycentrics.x * worldNormal0 +
        barycentrics.y * worldNormal1 +
        barycentrics.z * worldNormal2;
    input.tangent = float4(
        barycentrics.x * worldTangent0 +
        barycentrics.y * worldTangent1 +
        barycentrics.z * worldTangent2,
        barycentrics.x * vertex0.tw +
            barycentrics.y * vertex1.tw +
            barycentrics.z * vertex2.tw);
    input.uv =
        barycentrics.x * uv0 +
        barycentrics.y * uv1 +
        barycentrics.z * uv2;
    input.materialIdx = part.materialIdx;

    float2 uvDx = ReconstructUvGradient(
        int2(pixel),
        int2(1, 0),
        dimensions,
        identifiers,
        barycentrics,
        uv0,
        uv1,
        uv2);
    float2 uvDy = ReconstructUvGradient(
        int2(pixel),
        int2(0, 1),
        dimensions,
        identifiers,
        barycentrics,
        uv0,
        uv1,
        uv2);
    float3 normalDx = ReconstructNormalGradient(
        int2(pixel),
        int2(1, 0),
        dimensions,
        identifiers,
        barycentrics,
        worldNormal0,
        worldNormal1,
        worldNormal2);
    float3 normalDy = ReconstructNormalGradient(
        int2(pixel),
        int2(0, 1),
        dimensions,
        identifiers,
        barycentrics,
        worldNormal0,
        worldNormal1,
        worldNormal2);
    float3 barycentricsDx;
    float3 barycentricsDy;
    bool hasBarycentricsDx = TryLoadNeighborBarycentrics(
        int2(pixel),
        int2(1, 0),
        dimensions,
        identifiers,
        barycentricsDx);
    bool hasBarycentricsDy = TryLoadNeighborBarycentrics(
        int2(pixel),
        int2(0, 1),
        dimensions,
        identifiers,
        barycentricsDy);
    float edgeDistance = min(
        barycentrics.x,
        min(barycentrics.y, barycentrics.z));
    float edgeDx = hasBarycentricsDx
        ? min(
            barycentricsDx.x,
            min(barycentricsDx.y, barycentricsDx.z)) - edgeDistance
        : 0.0;
    float edgeDy = hasBarycentricsDy
        ? min(
            barycentricsDy.x,
            min(barycentricsDy.y, barycentricsDy.z)) - edgeDistance
        : 0.0;
    float barycentricWidth = abs(edgeDx) + abs(edgeDy);

    visibilityShadingOutput[pixel] = ShadePbrSurface(
        input,
        barycentrics,
        uvDx,
        uvDy,
        normalDx,
        normalDy,
        barycentricWidth);
}
