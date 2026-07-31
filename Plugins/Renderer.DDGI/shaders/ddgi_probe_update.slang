// SPDX-License-Identifier: MIT
// DDGI v1 probe-update compute kernel (canonical SEED-DDGI math).
//
// Dispatch shape: (MaxProbesTotalBudget, 1, 1, 1, 1, 1). One
// thread group is launched for every budget slot; each group first
// culls itself against the GPU-owned placement counter. Each live
// probe group loops over RaysPerProbe (= 32) rays,
// accumulates per-ray incident radiance + visibility Chebychev
// moments in registers, and on exit writes the 4-channel SH
// (Y00 + Y1_-1 + Y10 + Y11) to the irradiance atlas (register(u0))
// and the 2-channel mean/mean² moments to the visibility atlas
// (register(t4) RW).
//
// Direct-light contribution is gathered via a hierarchical BBV
// light tree (LightTreeNode at register(t2)) instead of an O(L)
// loop over the full Lights SSBO. The CPU builder (see
// Plugins/Renderer.DDGI/DDGILightTreeBuilder.cs) packs ≤ 4 lights
// per leaf and reorders the Lights SSBO so leaf ranges are
// contiguous; the shader therefore doesn't need a separate
// remapping buffer.
//
// Push constants carry:
//   * uint frameNumber, lightCount, raysPerProbe, maxProbeBudget
//   * GPU-owned probeCounter pointer for active-count culling
//   * TreeRootIndex, TreeNodeCount, TreeLeafVisitBudget

#include "scene_data.slang"

static const uint kLeafFlagBit   = 0x80000000u;
static const uint kLeafMaskBits  = 0x7FFFFFFFu;

// MARK: LightTreeNode descriptor (32 bytes, matches Plugins/
// Renderer.DDGI/DDGILightTreeNode.cs). High bit of
// .minAndData0.w acts as the leaf flag; remaining 31 bits hold
// either the child-left index or the leaf's first-light index.
struct LightTreeNode
{
    float4 minAndData0;
    float4 maxAndData1;

    bool   IsLeaf()             { return (asuint(minAndData0.w) & kLeafFlagBit) != 0u; }
    uint   FirstOrChildLeft()   { return asuint(minAndData0.w) & kLeafMaskBits; }
    uint   CountOrChildRight()  { return asuint(maxAndData1.w); }
};

struct UpdatePushData
{
    uint   probeCount;
    uint   frameNumber;
    uint   lightCount;
    uint   raysPerProbe;
    float4 cameraPositionAndJitter;
    float4 atlasUVParams;
    float4 originAndProbeCountZ;
    float4 extent;
    uint   probeIndex0;
    uint   probeIndex1;
    uint   probeIndex2;
    uint   probeIndex3;
    uint   probeIndex4;
    uint   probeIndex5;
    uint   probeIndex6;
    uint   probeIndex7;
    uint   treeRootIndex;
    uint   treeNodeCount;
    uint   treeLeafVisitBudget;
    uint   paddingTail3;
    uint   maxProbeBudget;
    uint   UseSceneTlas;
    uint   Padding0;
    uint   Padding1;
    uint   Padding2;

    float4* probePositions;
    LightData* lights;
    LightTreeNode* lightTree;
    uint*   probeCounter;
};

[push_constant]
ConstantBuffer<UpdatePushData> push;

static const int kMaxProbesPerFrame = 8;

// (Removed global registers)
RaytracingAccelerationStructure sceneTlas   : register(t3);

RWTexture2D<float4> irradiance : register(u0);
RWTexture2D<float4> visibility : register(u4);

// MARK: SH basis constants (canonical 2-band spherical-harmonics)
static const float kY00  = 0.282095;
static const float kY1m1 = 0.488603;

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
        float falloff = 1.0 - saturate(dist / max(range, 0.001));
        irradiance = light.color.xyz * intensity * dotNL *
            falloff / max(dist * dist, 0.001);
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
        float innerCos = clamp(light.shapeParams.x, 0.001, outerCos - 0.001);
        float cone = saturate((cosTheta - outerCos) /
            max(innerCos - outerCos, 0.001));
        float falloff = cone *
            (1.0 - saturate(dist / max(range, 0.001)));
        irradiance = light.color.xyz * intensity * dotNL * falloff /
            max(dist * dist, 0.001);
    }
    return irradiance;
}

// MARK: AABB slab test from ray origin in direction dir (assumed
// roughly unit length). Returns true when the ray reaches the AABB
// in front of the origin and within tFar. Used as a cheap node
// culling step before descending into the tree.
bool RayPlausiblyReachesAabb(
    float3 origin,
    float3 dir,
    float3 aabbMin,
    float3 aabbMax)
{
    float3 dirSign = float3(
        sign(dir.x), sign(dir.y), sign(dir.z));
    float3 invDirSafe = dirSign /
        max(abs(dir), float3(1e-6));
    float3 t1 = (aabbMin - origin) * invDirSafe;
    float3 t2 = (aabbMax - origin) * invDirSafe;
    float3 tNear3 = min(t1, t2);
    float3 tFar3  = max(t1, t2);
    float tNear = max(max(tNear3.x, tNear3.y), tNear3.z);
    float tFar  = min(min(tFar3.x, tFar3.y), tFar3.z);
    return tFar >= max(tNear, 0.0);
}

// MARK: Hierarchical light gather. Iterative worklist descents
// the tree, culls nodes whose world AABB the ray plausibility
// test misses, and accumulates per-leaf contributions up to
// `treeLeafVisitBudget`. When the tree is empty, returns zero so
// the caller falls back to ambient only.
float3 TraverseLightTree(
    float3 shadingPos,
    float3 shadingNormal,
    float3 rayOrigin,
    float3 rayDir,
    uint rootIdx,
    uint nodeCount,
    uint leafBudget)
{
    if (rootIdx >= nodeCount || nodeCount == 0 || leafBudget == 0)
        return float3(0.0);

    float3 accumulated = float3(0.0);
    const int kWorklistCap = 12;
    uint worklist[kWorklistCap];
    int top = 1;
    worklist[0] = rootIdx;
    uint leavesVisited = 0;

    while (top > 0 && leavesVisited < leafBudget)
    {
        uint nIdx = worklist[--top];
        if (nIdx >= nodeCount) continue;

        LightTreeNode node = push.lightTree[nIdx];

        // The surface normal is not a direction from the probe toward a
        // light. Applying the ray/AABB test here rejected valid lights
        // whenever the light tree lay outside that arbitrary normal ray.
        // The bounded leaf budget already limits traversal work; evaluate
        // each visited leaf instead of applying the invalid directional cull.
        if (node.IsLeaf())
        {
            uint first = node.FirstOrChildLeft();
            uint count = node.CountOrChildRight();
            for (uint i = 0u; i < count; ++i)
            {
                if (first + i >= push.lightCount) break;
                accumulated += EvaluateOneLight(shadingPos, shadingNormal, push.lights[first + i]);
            }
            ++leavesVisited;
            continue;
        }

        if (top + 2 <= kWorklistCap)
        {
            uint left = node.FirstOrChildLeft();
            uint right = node.CountOrChildRight();
            worklist[top++] = left;
            worklist[top++] = right;
        }
    }

    return accumulated;
}

groupshared float3 g_s0[32];
groupshared float3 g_sY[32];
groupshared float3 g_sZ[32];
groupshared float3 g_sX[32];
groupshared float2 g_visMoments[32];
groupshared uint   g_visHitCount[32];

[shader("compute")]
[NumThreads(32, 1, 1)]
void computeMain(
    uint3 groupID : SV_GroupID,
    uint3 groupThreadID : SV_GroupThreadID)
{
    uint probeOrdinal = groupID.x;
    uint rayIdx = groupThreadID.x;
    uint activeProbeCount = min(push.probeCounter[0], push.maxProbeBudget);

    if (probeOrdinal >= activeProbeCount || rayIdx >= 32u)
        return;

    // Placement allocates sparse slots densely from zero. The update pass
    // dispatches the fixed GPU budget and culls its tail from the GPU-owned
    // counter, so no CPU probe list or readback is required.
    uint sparseIdx = probeOrdinal;

    float3 origin = push.probePositions[sparseIdx].xyz;
    uint raysPerProbe = push.raysPerProbe;
    if (raysPerProbe == 0) raysPerProbe = 32;
    float frameJitter = push.cameraPositionAndJitter.w;

    float tMax = push.extent.x;
    if (tMax <= 0.0) tMax = 8.0;

    float3 rayDir = OctahedralDirection(rayIdx, raysPerProbe, frameJitter);

    RayDesc ray;
    ray.Origin    = origin;
    ray.Direction = rayDir;
    ray.TMin      = 0.01;
    ray.TMax      = tMax;

    bool hit = false;
    float hit_t = tMax;

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
        }
    }

    float3 incident = float3(0.0);
    if (hit)
    {
        g_visMoments[rayIdx] = float2(hit_t, hit_t * hit_t);
        g_visHitCount[rayIdx] = 1u;

        float3 hitPos = origin + rayDir * hit_t;
        float3 hitNormal = -rayDir;
        float3 incidentLight = TraverseLightTree(
            hitPos, hitNormal,
            hitPos, hitNormal,
            push.treeRootIndex,
            push.treeNodeCount,
            push.treeLeafVisitBudget);

        incident = incidentLight * (0.5 / 3.14159265);
    }
    else
    {
        g_visMoments[rayIdx] = float2(0.0, 0.0);
        g_visHitCount[rayIdx] = 0u;

        float upness = saturate(rayDir.y);
        float3 skySky   = float3(0.10, 0.14, 0.28);
        float3 skyHoriz = float3(0.18, 0.20, 0.24);
        incident = lerp(skyHoriz, skySky, upness) * 0.3;

        if (push.lightCount > 0 && rayDir.y > 0.0)
        {
            LightData sun = push.lights[0];
            if (sun.direction.w == 0.0)
            {
                float3 sunDir = -sun.direction.xyz;
                float sunDot = max(dot(rayDir, sunDir), 0.0);
                float sunAngle = pow(sunDot, 64.0);
                incident += sun.color.xyz * sun.color.w * sunAngle * 0.5;
            }
        }
    }

    g_s0[rayIdx] = incident;
    g_sY[rayIdx] = incident * rayDir.y;
    g_sZ[rayIdx] = incident * rayDir.z;
    g_sX[rayIdx] = incident * rayDir.x;

    GroupMemoryBarrierWithGroupSync();

    if (rayIdx == 0u)
    {
        float visMeanAccum = 0.0;
        float visMeanSqAccum = 0.0;
        uint  visCount = 0;
        float3 s0 = float3(0.0);
        float3 sY = float3(0.0);
        float3 sZ = float3(0.0);
        float3 sX = float3(0.0);

        for (uint i = 0u; i < 32u; ++i)
        {
            if (g_visHitCount[i] > 0u)
            {
                visMeanAccum   += g_visMoments[i].x;
                visMeanSqAccum += g_visMoments[i].y;
                visCount       += g_visHitCount[i];
            }
            s0 += g_s0[i];
            sY += g_sY[i];
            sZ += g_sZ[i];
            sX += g_sX[i];
        }

        uint visBase = sparseIdx;
        if (visCount > 0u)
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
        float PI = 3.14159265;
        float weight = 4.0 * PI * invRays;

        irradiance[uint2(baseCol + 0u, 0)] = float4(s0 * weight * kY00, 1.0);
        irradiance[uint2(baseCol + 1u, 0)] = float4(sY * weight * kY1m1, 0.0);
        irradiance[uint2(baseCol + 2u, 0)] = float4(sZ * weight * kY1m1, 0.0);
        irradiance[uint2(baseCol + 3u, 0)] = float4(sX * weight * kY1m1, 0.0);
    }
}
