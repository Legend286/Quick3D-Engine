// SPDX-License-Identifier: MIT
// DDGI v1 probe-update compute kernel (canonical SEED-DDGI math).
//
// Dispatch shape: (MaxProbesPerFrame, 1, 1, 1, 1, 1). One thread
// per probe. Each thread loops over RaysPerProbe (= 32) rays,
// accumulates per-ray incident radiance + visibility Chebychev
// moments in registers, and on exit writes the 4-channel SH
// (Y00 + Y1_-1 + Y10 + Y11) to the irradiance atlas (register(u0))
// and the 2-channel mean/mean² moments to the visibility atlas
// (register(t4) RW).
//
// Push constants declare:
//   * uint lightCount
//   * uint probeIndices[8] (sparse indexes into probePositions)
//   * The probe counted at probeIndices[GroupID.x] drives the
//     thread; pad slots holding -1 get skipped.

struct UpdatePushData
{
    uint   probeCount;
    uint   frameNumber;
    uint   padding0;
    uint   padding1;
    uint   lightCount;
    uint   raysPerProbe;
    uint   padding2;
    uint   padding3;
    float4 cameraPositionAndJitter;
    float4 atlasUVParams;        // x: irradSlot, y: visSlot, z: padding, w: padding
    float4 originAndProbeCountZ; // xyz: volume origin, w: probeCount (debug)
    float4 extent;               // x: maxProbeRadius, y: padding, z: padding, w: padding
};

[push_constant]
ConstantBuffer<UpdatePushData> push;

// Probe index queue lives inline in the push struct (slot 0) so
// Slang/HLSL does not treat it as a thread-local constant-initialised
// to zero. Reads here go directly through the matched C# struct
// fields; bump kMax if MaxProbesPerFrame ever grows past eight.

static const int kMaxProbesPerFrame = 8;

StructuredBuffer<float3> probePositions : register(t5);
StructuredBuffer<LightData> lights : register(t1);
RaytracingAccelerationStructure sceneTlas : register(t3);

RWTexture2D<float4> irradiance : register(u0);
RWTexture2D<float4> visibility : register(u4);

// MARK: SH basis constants (canonical 2-band spherical-harmonics)
const float kY00  = 0.282095;
const float kY1m1 = 0.488603;

float3 OctahedralDirection(uint sampleIdx, uint raysCount, float jitter)
{
    float u = (float(sampleIdx) + jitter) / float(raysCount);
    float v = (float((sampleIdx * 2654435761u) % raysCount) + jitter) /
        float(raysCount);
    float phi     = u * 2.0 * 3.14159265;
    float cosTheta = 1.0 - 2.0 * v;
    float sinTheta = sqrt(max(1.0 - cosTheta * cosTheta, 0.0));
    return float3(sinTheta * cos(phi),
                  sinTheta * sin(phi),
                  cosTheta);
}

float3 EvaluateLights(
    float3 dirToLightShadingPoint,
    StructuredBuffer<LightData> lightBuffer,
    uint lightCount)
{
    float3 accumulated = float3(0.0);
    for (uint i = 0; i < lightCount; ++i)
    {
        LightData light = lightBuffer[i];
        float lightType = light.direction.w;
        float intensity = light.color.w;

        float3 irradiance = float3(0.0);
        if (lightType == 0.0)
        {
            float3 n = -light.direction.xyz;
            float dotNL = max(0.0, dot(dirToLightShadingPoint, n));
            irradiance = light.color.xyz * intensity * dotNL;
        }
        else if (lightType == 1.0)
        {
            float range = light.position.w;
            float3 toLight = light.position.xyz - dirToLightShadingPoint;
            float dist = length(toLight);
            float falloff = 1.0 - saturate(dist / max(range, 0.001));
            irradiance = light.color.xyz * intensity *
                falloff / max(dist * dist, 0.001);
        }
        else if (lightType == 2.0)
        {
            float range = light.position.w;
            float3 axis = normalize(light.direction.xyz);
            float3 toLight = light.position.xyz - dirToLightShadingPoint;
            float dist = length(toLight);
            float cosTheta = dot(-toLight / max(dist, 0.001), axis);
            float outerCos = clamp(light.shapeParams.y, 0.001, 0.999);
            float innerCos = clamp(light.shapeParams.x, 0.001, outerCos - 0.001);
            float cone = saturate((cosTheta - outerCos) /
                max(innerCos - outerCos, 0.001));
            float falloff = cone *
                (1.0 - saturate(dist / max(range, 0.001)));
            irradiance = light.color.xyz * intensity * falloff /
                max(dist * dist, 0.001);
        }
        accumulated += irradiance;
    }
    return accumulated;
}

[shader("compute")]
[NumThreads(1, 1, 1)]
void computeMain(uint3 tid : SV_DispatchThreadID)
{
    uint probeOrdinal = tid.x;
    if (probeOrdinal >= push.probeCount)
        return;

    uint sparseIdx;
    switch (probeOrdinal)
    {
        case 0u: sparseIdx = push.probeIndex0; break;
        case 1u: sparseIdx = push.probeIndex1; break;
        case 2u: sparseIdx = push.probeIndex2; break;
        case 3u: sparseIdx = push.probeIndex3; break;
        case 4u: sparseIdx = push.probeIndex4; break;
        case 5u: sparseIdx = push.probeIndex5; break;
        case 6u: sparseIdx = push.probeIndex6; break;
        case 7u: sparseIdx = push.probeIndex7; break;
        default: sparseIdx = uint(-1); break;
    }
    if (sparseIdx == uint(-1))
        return;

    float3 probePos = probePositions[sparseIdx];

    uint raysPerProbe = push.raysPerProbe;
    if (raysPerProbe == 0) raysPerProbe = 32;
    uint lightCount = push.lightCount;

    float frameJitter = push.cameraPositionAndJitter.w;

    float visMeanAccum = 0.0;
    float visMeanSqAccum = 0.0;
    uint  visCount = 0;

    float sR = 0, sG = 0, sB = 0;
    float sY_R = 0, sY_G = 0, sY_B = 0;
    float sZ_R = 0, sZ_G = 0, sZ_B = 0;
    float sX_R = 0, sX_G = 0, sX_B = 0;

    float tMax = push.extent.x;
    if (tMax <= 0.0) tMax = 8.0;

    for (uint rayIdx = 0; rayIdx < raysPerProbe; ++rayIdx)
    {
        float3 rayDir = OctahedralDirection(
            rayIdx, raysPerProbe, frameJitter);

        RayDesc ray;
        ray.Origin    = probePos;
        ray.Direction = rayDir;
        ray.TMin      = 0.01;
        ray.TMax      = tMax;

        bool hit = false;
        float hit_t = tMax;

        RayQuery query;
        RayQueryInitialize(query, sceneTlas,
            RayFlagsFromVisibility(0xFF, RayQueryFlags.None),
            0xFF, ray);
        while (RayQueryProceed(query))
        {
            if (RayQueryCandidateType(query) ==
                RayQueryCandidateType.Triangle)
            {
                float candidateT =
                    RayQueryCandidateTriangleRayT(query);
                if (candidateT < hit_t)
                {
                    hit_t = candidateT;
                    hit = true;
                }
                RayQueryConfirm(query);
            }
        }
        if (RayQueryCommittedStatus(query) ==
            RayQueryCommittedStatus.Triangle)
        {
            hit = true;
            hit_t = min(hit_t, tMax);
        }
        else
        {
            hit = false;
        }

        if (hit)
        {
            visMeanAccum   += hit_t;
            visMeanSqAccum += hit_t * hit_t;
            ++visCount;
        }
        else
        {
            float3 incidentLight   = EvaluateLights(rayDir, lights, lightCount);
            float3 incidentAmbient = float3(0.20, 0.22, 0.25);
            float3 incident = incidentLight + incidentAmbient;

            sR  += incident.x;
            sG  += incident.y;
            sB  += incident.z;
            sY_R += incident.x * rayDir.y;
            sY_G += incident.y * rayDir.y;
            sY_B += incident.z * rayDir.y;
            sZ_R += incident.x * rayDir.z;
            sZ_G += incident.y * rayDir.z;
            sZ_B += incident.z * rayDir.z;
            sX_R += incident.x * rayDir.x;
            sX_G += incident.y * rayDir.x;
            sX_B += incident.z * rayDir.x;
        }
    }

    uint visBase = sparseIdx;
    if (visCount > 0)
    {
        float invCount = 1.0 / float(visCount);
        visibility[uint2(visBase, 0)] = float4(
            visMeanAccum * invCount,
            visMeanSqAccum * invCount,
            0.0, 0.0);
    }
    else
    {
        visibility[uint2(visBase, 0)] = float4(tMax, tMax * tMax, 0.0, 0.0);
    }

    float invRays = 1.0 / float(raysPerProbe);
    uint  baseCol  = sparseIdx * 4u;

    irradiance[uint2(baseCol + 0u, 0)] = float4(
        sR  * invRays * kY00,
        sG  * invRays * kY00,
        sB  * invRays * kY00, 1.0);
    irradiance[uint2(baseCol + 1u, 0)] = float4(
        sY_R * invRays * kY1m1,
        sY_G * invRays * kY1m1,
        sY_B * invRays * kY1m1, 0.0);
    irradiance[uint2(baseCol + 2u, 0)] = float4(
        sZ_R * invRays * kY1m1,
        sZ_G * invRays * kY1m1,
        sZ_B * invRays * kY1m1, 0.0);
    irradiance[uint2(baseCol + 3u, 0)] = float4(
        sX_R * invRays * kY1m1,
        sX_G * invRays * kY1m1,
        sX_B * invRays * kY1m1, 0.0);
}
