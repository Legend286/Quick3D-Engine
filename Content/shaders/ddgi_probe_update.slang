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
// Direct-light contribution is gathered via a hierarchical BBV
// light tree (LightTreeNode at register(t2)) instead of an O(L)
// loop over the full Lights SSBO. The CPU builder (see
// Plugins/Renderer.DDGI/DDGILightTreeBuilder.cs) packs ≤ 4 lights
// per leaf and reorders the Lights SSBO so leaf ranges are
// contiguous; the shader therefore doesn't need a separate
// remapping buffer.
//
// Push constants carry:
//   * uint probeCount, frameNumber, lightCount, raysPerProbe
//   * ProbeIndex0..7 inline
//   * TreeRootIndex, TreeNodeCount, TreeLeafVisitBudget

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
};

[push_constant]
ConstantBuffer<UpdatePushData> push;

static const int kMaxProbesPerFrame = 8;

StructuredBuffer<float3>    probePositions : register(t5);
StructuredBuffer<LightData> lights         : register(t1);
StructuredBuffer<LightTreeNode> lightTree  : register(t2);
RaytracingAccelerationStructure sceneTlas   : register(t3);

RWTexture2D<float4> irradiance : register(u0);
RWTexture2D<float4> visibility : register(u4);

// MARK: SH basis constants (canonical 2-band spherical-harmonics)
const float kY00  = 0.282095;
const float kY1m1 = 0.488603;

const uint kLeafFlagBit   = 0x80000000u;
const uint kLeafMaskBits  = 0x7FFFFFFFu;

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
    float3 dirToLightShadingPoint,
    LightData light)
{
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
    float3 invDirSafe = 1.0 / max(abs(dir), float3(1e-6));
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
    float3 probePos,
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

        LightTreeNode node = lightTree[nIdx];

        if (!RayPlausiblyReachesAabb(
            probePos, rayDir,
            node.minAndData0.xyz,
            node.maxAndData1.xyz))
        {
            continue;
        }

        if (node.IsLeaf())
        {
            uint first = node.FirstOrChildLeft();
            uint count = node.CountOrChildRight();
            for (uint i = 0u; i < count; ++i)
            {
                if (first + i >= push.lightCount) break;
                accumulated += EvaluateOneLight(rayDir, lights[first + i]);
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
            float3 incidentLight =
                TraverseLightTree(
                    probePos, rayDir,
                    push.treeRootIndex,
                    push.treeNodeCount,
                    push.treeLeafVisitBudget);
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
