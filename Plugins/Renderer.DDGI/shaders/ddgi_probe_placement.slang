// SPDX-License-Identifier: MIT
// DDGI sparse placement kernel.
//
// One thread handles each coarse volume cell. The GPU writes dense probe
// positions, coarse-grid indirection, active count, and indirect draw args.
// Probe acceptance is based on the TLAS free-space test. Scene AABBs are
// uploaded for ABI compatibility and diagnostics, but do not cull empty cells:
// empty cells are required for a usable irradiance volume.

RaytracingAccelerationStructure sceneTlas : register(t3);

struct MeshAabb
{
    float4 Min;
    float4 Max;
};

struct PlacementPushData
{
    float4 VolumeMinAndCellCount;
    float4 VolumeCellSizeAndMaxDepth;
    float4 ProbeBudgetAndParams;
    MeshAabb* meshAabbs;
    float4* probePositions;
    int* gridToProbeIndex;
    uint* probeCounter;
    uint* probeDrawArgs;
    uint UseSceneTlas;
    uint Padding0;
    uint Padding1;
    uint Padding2;
};

[push_constant]
ConstantBuffer<PlacementPushData> push;

bool ValidateFreeSpace(
    float3 candidateWorldPos,
    float freeSpaceRadius,
    float insideEpsilon)
{
    RayDesc ray;
    ray.Origin = candidateWorldPos;
    ray.TMin = 0.001;

    static const float3 directions[6] = {
        float3( 1, 0, 0), float3(-1, 0, 0),
        float3( 0, 1, 0), float3( 0,-1, 0),
        float3( 0, 0, 1), float3( 0, 0,-1),
    };

    bool escaped = false;
    bool insideWall = false;
    for (int dirIndex = 0; dirIndex < 6; ++dirIndex)
    {
        ray.Direction = directions[dirIndex];
        ray.TMax = freeSpaceRadius;

        RayQuery<RAY_FLAG_FORCE_OPAQUE> query;
        query.TraceRayInline(sceneTlas, RAY_FLAG_FORCE_OPAQUE, 0xFF, ray);
        while (query.Proceed())
        {
        }

        if (query.CommittedStatus() == COMMITTED_TRIANGLE_HIT)
        {
            if (query.CommittedRayT() < insideEpsilon)
                insideWall = true;
        }
        else
        {
            escaped = true;
        }
    }

    return !insideWall && escaped;
}

[shader("compute")]
[NumThreads(8, 8, 8)]
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
    uint maxProbes = (uint)push.ProbeBudgetAndParams.x;
    float freeSpaceRadius = push.ProbeBudgetAndParams.y;
    float insideEpsilon = push.ProbeBudgetAndParams.z;

    float3 cellMin = volumeMin + float3(cell) * cellSize;
    float3 cellMax = cellMin + cellSize;
    float3 center = (cellMin + cellMax) * 0.5;
    float3 validPos = center;
    bool foundValid = push.UseSceneTlas == 0u ||
        ValidateFreeSpace(center, freeSpaceRadius, insideEpsilon);

    if (foundValid)
    {
        uint slotIndex;
        InterlockedAdd(push.probeCounter[0], 1u, slotIndex);
        if (slotIndex >= maxProbes)
        {
            push.gridToProbeIndex[coarseLinearIdx] = -1;
            return;
        }

        push.probePositions[slotIndex] = float4(validPos, 1.0);
        push.gridToProbeIndex[coarseLinearIdx] = (int)slotIndex;
        if (slotIndex == 0u)
            push.probeDrawArgs[1] = 1u;
        InterlockedAdd(push.probeDrawArgs[0], 24u);
    }
    else
    {
        push.gridToProbeIndex[coarseLinearIdx] = -1;
    }
}
