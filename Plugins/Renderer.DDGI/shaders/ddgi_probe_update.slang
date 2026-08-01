// SPDX-License-Identifier: MIT
#include "scene_data.slang"
#include "nishita_sky.slang"

struct ProbeState
{
    uint flags;
    uint lastUpdateFrame;
    uint geometryRevision;
    uint lightRevision;
};

static const uint kRadianceConvergenceMask = 7u << 10u;
static const uint kRadianceConvergenceShift = 10u;
static const uint kRadianceConvergenceSteps = 3u;
static const uint kInitialRadianceAccumulation = 1u << 13u;
static const float kVisibilityLobePower = 4.0;

struct UpdatePushData
{
    uint   frameNumber;
    uint   lightCount;
    uint   raysPerProbe;
    uint   maxUpdates;
    float4 cameraPositionAndJitter;
    float4 atlasUVParams;
    float4 originAndProbeCountZ;
    float4 extent;
    float4 skySunDirectionAndRadius;
    float4 skyAtmosphereParameters;
    uint   maxProbeBudget;
    uint   UseSceneTlas;
    uint   radianceRevision;
    float  minimumProbeClearance;

    float4* probePositions;
    LightData* lights;
    uint*   probeCounter;
    ProbeState* probeStates;
    uint* probeUpdateQueue;
    InstanceData* instances;
    PartData* parts;
    MaterialData* materials;
    uint instanceCount;
    uint partCount;
    uint materialCount;
    uint padding0;
};

[push_constant]
ConstantBuffer<UpdatePushData> push;

[[vk::binding(3, 0)]]
RaytracingAccelerationStructure sceneTlas   : register(t3);

[[vk::binding(0, 0)]] RWTexture2D<float4> irradiance : register(u0);
[[vk::binding(4, 0)]] RWTexture2D<float4> visibility : register(u4);

struct BindlessHeap
{
    Texture2D textures[4096];
};
[[vk::binding(1, 0)]] ParameterBlock<BindlessHeap> bindless;
[[vk::binding(2, 0)]] SamplerState textureSampler;

// MARK: SH basis constants (canonical 2-band spherical-harmonics)
static const float kY00  = 0.282095;
static const float kY1m1 = 0.488603;

uint2 AtlasCoordinate(uint linearIndex)
{
    uint width = max((uint)push.atlasUVParams.z, 1u);
    return uint2(linearIndex % width, linearIndex / width);
}

uint HashUint(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    value ^= value >> 16u;
    return value;
}

float Hash01(uint value)
{
    return float(HashUint(value) & 0x00ffffffu) / 16777216.0;
}

float3 StratifiedSphereDirection(
    uint sampleIdx,
    uint raysCount,
    uint probeIndex,
    uint frameNumber)
{
    uint seed = HashUint(
        probeIndex ^ HashUint(frameNumber + 0x9e3779b9u));
    float heightJitter = Hash01(seed);
    float azimuthRotation = Hash01(seed ^ 0x68bc21ebu);
    float height =
        (float(sampleIdx) + heightJitter) / float(raysCount);
    float azimuth = frac(
        float(sampleIdx) * 0.61803398875 + azimuthRotation);
    float phi = azimuth * 2.0 * 3.14159265;
    float cosTheta = 1.0 - 2.0 * height;
    float sinTheta = sqrt(max(1.0 - cosTheta * cosTheta, 0.0));
    return float3(sinTheta * cos(phi),
                  sinTheta * sin(phi),
                  cosTheta);
}

float3 EvaluateOneLight(
    float3 shadingPos,
    float3 shadingNormal,
    LightData light)
{
    float lightType = light.direction.w;
    float intensity = light.color.w;

    float3 irradiance = float3(0.0);
    if (lightType == 0.0)
    {
        float3 n = -light.direction.xyz;
        float dotNL = max(0.0, dot(shadingNormal, n));
        irradiance = light.color.xyz * intensity * dotNL;
    }
    else if (lightType == 1.0)
    {
        float range = light.position.w;
        float3 toLight = light.position.xyz - shadingPos;
        float dist = length(toLight);
        float3 l = toLight / max(dist, 0.001);
        float dotNL = max(0.0, dot(shadingNormal, l));
        float falloff = saturate(
            1.0 - pow(dist / max(range, 0.001), 4.0));
        irradiance = light.color.xyz * intensity * dotNL *
            falloff * falloff / (dist * dist + 1.0);
    }
    else if (lightType == 2.0)
    {
        float range = light.position.w;
        float3 axis = normalize(light.direction.xyz);
        float3 toLight = light.position.xyz - shadingPos;
        float dist = length(toLight);
        float3 l = toLight / max(dist, 0.001);
        float dotNL = max(0.0, dot(shadingNormal, l));
        float cosTheta = dot(-l, axis);
        float outerCos = clamp(light.shapeParams.y, 0.001, 0.999);
        float innerCos = clamp(light.shapeParams.x, 0.001, 0.999);
        float cone = saturate((cosTheta - outerCos) /
            max(innerCos - outerCos, 0.001));
        float rangeFalloff = saturate(
            1.0 - pow(dist / max(range, 0.001), 4.0));
        float falloff = cone * cone * rangeFalloff * rangeFalloff;
        irradiance = light.color.xyz * intensity * dotNL * falloff /
            (dist * dist + 1.0);
    }
    return irradiance;
}

float3 EvaluateLights(
    float3 shadingPos,
    float3 shadingNormal)
{
    float3 directAccum = float3(0.0);
    for (uint lightIndex = 0u;
         lightIndex < push.lightCount;
         ++lightIndex)
    {
        directAccum += EvaluateOneLight(
            shadingPos,
            shadingNormal,
            push.lights[lightIndex]);
    }
    return directAccum;
}

uint LoadTriangleIndex(PartData part, uint elementIndex)
{
    if ((part.flags & 1u) != 0u)
        return part.indices[elementIndex];
    uint packed = part.indices[elementIndex / 2u];
    return (elementIndex & 1u) == 0u
        ? packed & 0xFFFFu
        : packed >> 16u;
}

void EvaluateHitSurface(
    uint partIndex,
    uint primitiveIndex,
    float2 triangleBarycentrics,
    float3 rayDirection,
    out float3 worldNormal,
    out float3 diffuseReflectance,
    out float3 emissiveRadiance)
{
    worldNormal = -rayDirection;
    diffuseReflectance = float3(0.72);
    emissiveRadiance = float3(0.0);
    if (partIndex >= push.partCount)
        return;

    PartData part = push.parts[partIndex];
    if (part.instanceIdx >= push.instanceCount ||
        part.materialIdx >= push.materialCount)
    {
        return;
    }

    uint elementBase = primitiveIndex * 3u;
    if (elementBase + 2u >= part.indexCount)
        return;
    uint index0 = LoadTriangleIndex(part, elementBase + 0u);
    uint index1 = LoadTriangleIndex(part, elementBase + 1u);
    uint index2 = LoadTriangleIndex(part, elementBase + 2u);
    Vertex vertex0 = part.vertices[index0];
    Vertex vertex1 = part.vertices[index1];
    Vertex vertex2 = part.vertices[index2];
    float3 barycentrics = float3(
        1.0 - triangleBarycentrics.x - triangleBarycentrics.y,
        triangleBarycentrics.x,
        triangleBarycentrics.y);
    float3 localNormal = normalize(
        float3(vertex0.nx, vertex0.ny, vertex0.nz) * barycentrics.x +
        float3(vertex1.nx, vertex1.ny, vertex1.nz) * barycentrics.y +
        float3(vertex2.nx, vertex2.ny, vertex2.nz) * barycentrics.z);
    InstanceData instance = push.instances[part.instanceIdx];
    worldNormal = normalize(mul(
        (float3x3)instance.modelMatrix,
        localNormal));
    if (dot(worldNormal, rayDirection) > 0.0)
        worldNormal = -worldNormal;

    MaterialData material = push.materials[part.materialIdx];
    float2 uv =
        float2(vertex0.u, vertex0.v) * barycentrics.x +
        float2(vertex1.u, vertex1.v) * barycentrics.y +
        float2(vertex2.u, vertex2.v) * barycentrics.z;
    float3 albedo = material.baseColor.rgb;
    if (material.albedoTexIndex != 0xFFFFFFFFu)
    {
        albedo *= bindless.textures[material.albedoTexIndex].SampleLevel(
            textureSampler,
            uv,
            0.0).rgb;
    }
    float metallic = material.metallic;
    if (material.rmaTexIndex != 0xFFFFFFFFu)
    {
        metallic *= bindless.textures[material.rmaTexIndex].SampleLevel(
            textureSampler,
            uv,
            0.0).b;
    }
    diffuseReflectance = max(albedo, float3(0.0)) *
        saturate(1.0 - metallic);
    emissiveRadiance = max(material.emissiveColor.rgb, float3(0.0));
}

groupshared float3 g_s0[32];
groupshared float3 g_sY[32];
groupshared float3 g_sZ[32];
groupshared float3 g_sX[32];
groupshared float3 g_rayDirection[32];
groupshared float g_rayDistance[32];

static const uint kVisibilityTileResolution = 4u;
static const uint kVisibilityTexelsPerProbe = 16u;

float3 OctahedralDecode(float2 encoded)
{
    float2 octahedron = encoded * 2.0 - 1.0;
    float3 direction = float3(
        octahedron.x,
        octahedron.y,
        1.0 - abs(octahedron.x) - abs(octahedron.y));
    if (direction.z < 0.0)
    {
        float2 signValue = float2(
            direction.x >= 0.0 ? 1.0 : -1.0,
            direction.y >= 0.0 ? 1.0 : -1.0);
        direction.xy =
            (1.0 - abs(direction.yx)) * signValue;
    }
    return normalize(direction);
}

[shader("compute")]
[NumThreads(32, 1, 1)]
void computeMain(
    uint3 groupID : SV_GroupID,
    uint3 groupThreadID : SV_GroupThreadID)
{
    uint probeOrdinal = groupID.x;
    uint rayIdx = groupThreadID.x;
    uint updateCount = min(push.probeCounter[1], push.maxUpdates);

    if (probeOrdinal >= updateCount || rayIdx >= 32u)
        return;

    uint sparseIdx = push.probeUpdateQueue[probeOrdinal];
    if (sparseIdx >= push.maxProbeBudget)
        return;

    float3 origin = push.probePositions[sparseIdx].xyz;
    uint raysPerProbe = push.raysPerProbe;
    if (raysPerProbe == 0) raysPerProbe = 32;

    float tMax = push.extent.x;
    if (tMax <= 0.0) tMax = 8.0;

    float3 rayDir = StratifiedSphereDirection(
        rayIdx,
        raysPerProbe,
        sparseIdx,
        push.frameNumber);

    RayDesc ray;
    ray.Origin    = origin;
    ray.Direction = rayDir;
    ray.TMin      = 0.01;
    ray.TMax      = tMax;

    bool hit = false;
    float hit_t = tMax;
    uint hitPartIndex = 0xFFFFFFFFu;
    uint hitPrimitiveIndex = 0u;
    float2 hitBarycentrics = float2(0.0);

    if (push.UseSceneTlas != 0u)
    {
        RayQuery<RAY_FLAG_FORCE_OPAQUE> query;
        query.TraceRayInline(sceneTlas, RAY_FLAG_FORCE_OPAQUE, 0xFF, ray);
        while (query.Proceed())
        {
        }
        if (query.CommittedStatus() == COMMITTED_TRIANGLE_HIT)
        {
            hit = true;
            hit_t = min(query.CommittedRayT(), tMax);
            hitPartIndex = query.CommittedInstanceID();
            hitPrimitiveIndex = query.CommittedPrimitiveIndex();
            hitBarycentrics = query.CommittedTriangleBarycentrics();
        }
    }

    float3 incident = float3(0.0);
    SkyParams sky;
    sky.sunDirAndRadius = push.skySunDirectionAndRadius;
    sky.intensityTurbidityAlbedoPad =
        push.skyAtmosphereParameters;
    SkyParams diffuseSky = sky;
    diffuseSky.sunDirAndRadius.w = 0.0;
    if (hit)
    {
        float3 hitPos = origin + rayDir * hit_t;
        float3 hitNormal;
        float3 diffuseReflectance;
        float3 emissiveRadiance;
        EvaluateHitSurface(
            hitPartIndex,
            hitPrimitiveIndex,
            hitBarycentrics,
            rayDir,
            hitNormal,
            diffuseReflectance,
            emissiveRadiance);
        float3 incidentLight = EvaluateLights(hitPos, hitNormal);
        float3 skyIrradiance = GetSkyRadiance(
            hitNormal,
            diffuseSky.sunDirAndRadius.xyz,
            diffuseSky) * 3.14159265;
        incident =
            (incidentLight + skyIrradiance) *
                diffuseReflectance * (1.0 / 3.14159265) +
            emissiveRadiance;
    }
    else
    {
        incident = GetSkyRadiance(
            rayDir,
            diffuseSky.sunDirAndRadius.xyz,
            diffuseSky);
    }

    g_s0[rayIdx] = incident;
    g_sY[rayIdx] = incident * rayDir.y;
    g_sZ[rayIdx] = incident * rayDir.z;
    g_sX[rayIdx] = incident * rayDir.x;
    g_rayDirection[rayIdx] = rayDir;
    g_rayDistance[rayIdx] = hit ? hit_t : tMax;

    GroupMemoryBarrierWithGroupSync();

    if (rayIdx == 0u)
    {
        float3 s0 = float3(0.0);
        float3 sY = float3(0.0);
        float3 sZ = float3(0.0);
        float3 sX = float3(0.0);

        for (uint i = 0u; i < 32u; ++i)
        {
            s0 += g_s0[i];
            sY += g_sY[i];
            sZ += g_sZ[i];
            sX += g_sX[i];
        }

        ProbeState state = push.probeStates[sparseIdx];
        bool hasHistory = push.probePositions[sparseIdx].w > 0.5;
        bool lightingChanged =
            state.lightRevision != push.radianceRevision;
        uint convergenceSteps =
            (state.flags & kRadianceConvergenceMask) >>
            kRadianceConvergenceShift;
        bool initialAccumulation =
            (state.flags & kInitialRadianceAccumulation) != 0u;
        bool replaceHistory = !hasHistory ||
            (state.flags & (4u | 16u)) != 0u;
        float historyWeight = 0.92;
        if (replaceHistory)
        {
            historyWeight = 0.0;
        }
        else if (lightingChanged)
        {
            historyWeight = 0.50;
        }
        else if (convergenceSteps > 0u)
        {
            float accumulatedBatchCount = float(
                kRadianceConvergenceSteps - convergenceSteps + 1u);
            historyWeight = initialAccumulation
                ? accumulatedBatchCount / (accumulatedBatchCount + 1.0)
                : 0.50;
        }
        uint visibilityBase =
            sparseIdx * kVisibilityTexelsPerProbe;
        for (uint tileY = 0u;
             tileY < kVisibilityTileResolution;
             ++tileY)
        {
            for (uint tileX = 0u;
                 tileX < kVisibilityTileResolution;
                 ++tileX)
            {
                float2 octahedralUv =
                    (float2(tileX, tileY) + 0.5) /
                    float(kVisibilityTileResolution);
                float3 tileDirection =
                    OctahedralDecode(octahedralUv);
                float weightSum = 0.0;
                float mean = 0.0;
                float meanSquare = 0.0;
                for (uint visibilityRay = 0u;
                     visibilityRay < 32u;
                     ++visibilityRay)
                {
                    float directionalWeight = pow(
                        saturate(dot(
                            tileDirection,
                            g_rayDirection[visibilityRay])),
                        kVisibilityLobePower);
                    float rayDistance =
                        g_rayDistance[visibilityRay];
                    weightSum += directionalWeight;
                    mean += rayDistance * directionalWeight;
                    meanSquare += rayDistance * rayDistance *
                        directionalWeight;
                }
                float inverseWeight = 1.0 /
                    max(weightSum, 0.0001);
                if (weightSum <= 0.0001)
                {
                    mean = tMax;
                    meanSquare = tMax * tMax;
                    inverseWeight = 1.0;
                }
                float4 nextVisibility = float4(
                    mean * inverseWeight,
                    meanSquare * inverseWeight,
                    0.0,
                    0.0);
                uint localVisibilityIndex =
                    tileY * kVisibilityTileResolution + tileX;
                uint2 visibilityCoordinate = AtlasCoordinate(
                    visibilityBase + localVisibilityIndex);
                float4 previousVisibility =
                    visibility[visibilityCoordinate];
                visibility[visibilityCoordinate] = lerp(
                    nextVisibility,
                    previousVisibility,
                    historyWeight);
            }
        }

        float invRays = 1.0 / float(raysPerProbe);
        uint  baseCol  = sparseIdx * 4u;
        float PI = 3.14159265;
        float weight = 4.0 * PI * invRays;

        float4 nextC0 = float4(s0 * weight * kY00, 1.0);
        float4 nextCY = float4(sY * weight * kY1m1, 0.0);
        float4 nextCZ = float4(sZ * weight * kY1m1, 0.0);
        float4 nextCX = float4(sX * weight * kY1m1, 0.0);
        uint2 coordinate0 = AtlasCoordinate(baseCol + 0u);
        uint2 coordinate1 = AtlasCoordinate(baseCol + 1u);
        uint2 coordinate2 = AtlasCoordinate(baseCol + 2u);
        uint2 coordinate3 = AtlasCoordinate(baseCol + 3u);
        irradiance[coordinate0] = lerp(
            nextC0,
            irradiance[coordinate0],
            historyWeight);
        irradiance[coordinate1] = lerp(
            nextCY,
            irradiance[coordinate1],
            historyWeight);
        irradiance[coordinate2] = lerp(
            nextCZ,
            irradiance[coordinate2],
            historyWeight);
        irradiance[coordinate3] = lerp(
            nextCX,
            irradiance[coordinate3],
            historyWeight);
        state.lastUpdateFrame = push.frameNumber;
        state.lightRevision = push.radianceRevision;
        state.flags &= ~4u;
        state.flags &= ~16u;
        state.flags &= ~kRadianceConvergenceMask;
        state.flags &= ~kInitialRadianceAccumulation;
        if (replaceHistory)
        {
            state.flags |=
                kRadianceConvergenceSteps <<
                kRadianceConvergenceShift;
            state.flags |= kInitialRadianceAccumulation;
        }
        else if (lightingChanged)
        {
            state.flags |=
                kRadianceConvergenceSteps <<
                kRadianceConvergenceShift;
        }
        else if (convergenceSteps > 0u)
        {
            uint remainingSteps = convergenceSteps - 1u;
            if (remainingSteps > 0u)
            {
                state.flags |=
                    remainingSteps <<
                    kRadianceConvergenceShift;
                if (initialAccumulation)
                    state.flags |= kInitialRadianceAccumulation;
            }
        }
        float nearestGeometryDistance = tMax;
        for (uint clearanceRay = 0u;
             clearanceRay < 32u;
             ++clearanceRay)
        {
            nearestGeometryDistance = min(
                nearestGeometryDistance,
                g_rayDistance[clearanceRay]);
        }
        if (push.UseSceneTlas != 0u &&
            nearestGeometryDistance < push.minimumProbeClearance)
        {
            state.flags |= 4u;
            state.flags |= 8u;
        }
        push.probeStates[sparseIdx] = state;
        push.probePositions[sparseIdx].w = 1.0;
    }
}
