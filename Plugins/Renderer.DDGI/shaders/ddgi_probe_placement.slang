// SPDX-License-Identifier: MIT
// DDGI sparse placement kernel.
//
// Dispatch shape: (CoarseGridResolution, CoarseGridResolution,
// CoarseGridResolution, 1, 1, 1). One thread per coarse cell of
// the volume's uniform AABB partition. Each thread descends its
// own octree sub-tree (lit by scene-mesh AABBs at register(t1)),
// validates leaves at MaxDepth via six outward rays against the
// host TLAS (register(t3)), and atomically allocates slots in
// the ProbePositions SSBO (register(t5)). The coarse-grid cell's
// indirection entry is set to the chosen slot index (or -1).
//
// Stack: depth is bounded by the host-side MaxOctreeDepth (= 3),
// so a fixed-size per-thread stack of 16 slots is sufficient for
// the worst-case frontier (depth + branch factor ≤ 3 + 8).
//
// Why not groupshared memory? Per-thread local arrays allocate
// in registers / spill to local memory, which is what we want
// anyway — groupshared would alias across the whole compute
// group (32 threads = one block) and is wrong for per-cell
// independent traversals.

StructuredBuffer<MeshAabb> meshAabbs : register(t1);

RWStructuredBuffer<float3> probePositions : register(t5);
RWStructuredBuffer<int>    gridToProbeIndex : register(t6);
RWStructuredBuffer<uint>   probeCounter : register(u0);

RaytracingAccelerationStructure sceneTlas : register(t3);

struct MeshAabb
{
    float4 Min;
    float4 Max;
};

struct PlacementPushData
{
    float4 VolumeMinAndCellCount;     // xyz: volumeMin, w: coarseGridRes (e.g. 32)
    float4 VolumeCellSizeAndMaxDepth; // xyz: cellSize, w: maxOctreeDepth
    float4 ProbeBudgetAndParams;      // x: maxProbes, y: freeSpaceR, z: insideEpsilon, w: meshAabbCount
};

[push_constant]
ConstantBuffer<PlacementPushData> push;

bool AabbIntersectsAabb(
    float3 aMin, float3 aMax,
    float3 bMin, float3 bMax)
{
    return all(aMin <= bMax) && all(bMin <= aMax);
}

bool NodeIntersectsScene(
    float3 nodeMin, float3 nodeMax)
{
    uint count = (uint)push.ProbeBudgetAndParams.w;
    for (uint i = 0; i < count; ++i)
    {
        float4 aMin = meshAabbs[i].Min;
        float4 aMax = meshAabbs[i].Max;
        if (AabbIntersectsAabb(
            nodeMin, nodeMax,
            aMin.xyz, aMax.xyz))
            return true;
    }
    return false;
}

bool ValidateFreeSpace(
    float3 candidateWorldPos,
    float freeSpaceRadius,
    float insideEpsilon)
{
    RayDesc ray;
    ray.Origin = candidateWorldPos;
    ray.TMin = 0.0001;
    bool escaped = false;
    bool insideWall = false;

    static const float3 directions[6] = {
        float3( 1, 0, 0), float3(-1, 0, 0),
        float3( 0, 1, 0), float3( 0,-1, 0),
        float3( 0, 0, 1), float3( 0, 0,-1),
    };

    for (int dirIndex = 0; dirIndex < 6; ++dirIndex)
    {
        ray.Direction = directions[dirIndex];
        ray.TMax = freeSpaceRadius;

        RayQuery query;
        RayQueryInitialize(query, sceneTlas,
            RayFlagsFromVisibility(0xFF, RayQueryFlags.None),
            0xFF, ray);
        while (RayQueryProceed(query))
        {
            if (RayQueryCandidateType(query) ==
                RayQueryCandidateType.Triangle)
            {
                float hitDistance =
                    RayQueryCandidateTriangleRayT(query);
                if (hitDistance < insideEpsilon)
                    insideWall = true;
                RayQueryConfirm(query);
            }
        }
        if (RayQueryCommittedStatus(query) ==
            RayQueryCommittedStatus.None)
            escaped = true;
    }

    return !insideWall && escaped;
}

[shader("compute")]
[NumThreads(1, 1, 1)]
void computeMain(uint3 cell : SV_DispatchThreadID)
{
    uint coarseRes = (uint)push.VolumeMinAndCellCount.w;
    if (any(cell >= uint3(coarseRes, coarseRes, coarseRes)))
        return;

    uint coarseLinearIdx =
        cell.z * coarseRes * coarseRes +
        cell.y * coarseRes + cell.x;

    float3 volumeMin = push.VolumeMinAndCellCount.xyz;
    float3 cellSize = push.VolumeCellSizeAndMaxDepth.xyz;
    float maxDepth = push.VolumeCellSizeAndMaxDepth.w;
    uint maxProbes = (uint)push.ProbeBudgetAndParams.x;
    float freeSpaceR = push.ProbeBudgetAndParams.y;
    float insideEps = push.ProbeBudgetAndParams.z;

    float3 cellMin = volumeMin + float3(cell) * cellSize;
    float3 cellMax = cellMin + cellSize;

    if (!NodeIntersectsScene(cellMin, cellMax))
    {
        gridToProbeIndex[coarseLinearIdx] = -1;
        return;
    }

    // Per-thread fixed stack of node descriptors. Sized for
    // MaxOctreeDepth up to 4 (worst-case frontier ≈ 15 nodes when
    // root expands to depth 2 and a single branch goes one level
    // deeper); bumping depth to 5 would push the DFS frontier to
    // 31 nodes, so any further growth must also bump this cap and
    // the overflow guard below.
    const int kStackCap = 32;
    float3 stkMin[kStackCap];
    float3 stkMax[kStackCap];
    uint   stkDepth[kStackCap];
    int top = 0;

    stkMin[0] = cellMin;
    stkMax[0] = cellMax;
    stkDepth[0] = 0;
    top = 1;

    int firstAccepted = -1;

    while (top > 0)
    {
        --top;
        float3 nMin = stkMin[top];
        float3 nMax = stkMax[top];
        uint depth = stkDepth[top];

        if (!NodeIntersectsScene(nMin, nMax))
            continue;

        if (depth >= (uint)maxDepth)
        {
            float3 center = (nMin + nMax) * 0.5;
            if (!ValidateFreeSpace(center, freeSpaceR, insideEps))
                continue;
            uint slot;
            InterlockedAdd(probeCounter[0], 1, slot);
            if (slot >= maxProbes)
            {
                InterlockedAdd(probeCounter[0], -1);
                continue;
            }
            probePositions[slot] = center;
            if (firstAccepted < 0)
                firstAccepted = (int)slot;
            continue;
        }

        float3 half = (nMax - nMin) * 0.5;
        for (int cz = 0; cz < 2; ++cz)
        for (int cy = 0; cy < 2; ++cy)
        for (int cx = 0; cx < 2; ++cx)
        {
            if (top >= kStackCap) continue;
            stkMin[top] = float3((float)cx, (float)cy, (float)cz) * half + nMin;
            stkMax[top] = stkMin[top] + half;
            stkDepth[top] = depth + 1;
            ++top;
        }
    }

    gridToProbeIndex[coarseLinearIdx] = firstAccepted;
}
