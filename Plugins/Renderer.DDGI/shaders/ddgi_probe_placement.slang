// SPDX-License-Identifier: MIT
#include "scene_data.slang"

[[vk::binding(3, 0)]]
RaytracingAccelerationStructure sceneTlas : register(t3);

struct ProbeState
{
    uint flags;
    uint lastUpdateFrame;
    uint geometryRevision;
    uint lightRevision;
};

struct ProbeRequest
{
    float4 worldPosition;
    uint probeSlot;
    uint gridCellIndex;
    uint clipmapLevel;
    uint flags;
};

static const uint kRadianceConvergenceMask = 7u << 10u;
static const uint kInitialRadianceAccumulation = 1u << 13u;

struct PlacementPushData
{
    float4x4 viewProjection;
    float4 cameraPositionAndFrame;
    float4 volumeExtentAndGridResolution;
    float4 probeBudgetAndParams;
    ProbeRequest* probeRequests;
    float4* probePositions;
    int* gridToProbeIndex;
    uint* probeCounter;
    ProbeState* probeStates;
    uint* volumeState;
    int4* probeWorldKeys;
    uint* worldProbeHash;
    InstanceData* instances;
    PartData* parts;
    uint requestCount;
    uint useSceneTlas;
    uint maxRelocationClassifications;
    uint maxSceneBakeClassifications;
    uint geometryRevision;
    uint clipmapLevelCount;
    uint worldProbeHashCapacity;
    uint instanceCount;
    uint partCount;
};

[push_constant]
ConstantBuffer<PlacementPushData> push;

uint HashProbeKey(int3 worldCell, uint level)
{
    uint hashValue = 2166136261u;
    hashValue = (hashValue ^ asuint(worldCell.x)) * 16777619u;
    hashValue = (hashValue ^ asuint(worldCell.y)) * 16777619u;
    hashValue = (hashValue ^ asuint(worldCell.z)) * 16777619u;
    hashValue = (hashValue ^ level) * 16777619u;
    hashValue ^= hashValue >> 16u;
    hashValue *= 0x7feb352du;
    hashValue ^= hashValue >> 15u;
    return hashValue;
}

void RegisterProbeKey(uint slot, int3 worldCell, uint level)
{
    push.probeWorldKeys[slot] = int4(worldCell, (int)level);
    uint hashCapacity = push.worldProbeHashCapacity;
    if (hashCapacity == 0u)
        return;
    uint hashIndex = HashProbeKey(worldCell, level) &
        (hashCapacity - 1u);
    for (uint attempt = 0u; attempt < 128u; ++attempt)
    {
        uint previous;
        InterlockedCompareExchange(
            push.worldProbeHash[hashIndex],
            0u,
            slot + 1u,
            previous);
        if (previous == 0u || previous == slot + 1u)
            return;
        hashIndex = (hashIndex + 1u) & (hashCapacity - 1u);
    }
}

float ClassifyGeometry(
    float3 position,
    float radius,
    float epsilon,
    out bool freeSpace,
    out float3 nearestDirection)
{
    if (push.useSceneTlas == 0u)
    {
        freeSpace = true;
        nearestDirection = float3(0.0, 1.0, 0.0);
        return radius;
    }

    static const float3 directions[14] = {
        float3(1, 0, 0), float3(-1, 0, 0),
        float3(0, 1, 0), float3(0, -1, 0),
        float3(0, 0, 1), float3(0, 0, -1),
        float3(0.577350269, 0.577350269, 0.577350269),
        float3(-0.577350269, 0.577350269, 0.577350269),
        float3(0.577350269, -0.577350269, 0.577350269),
        float3(0.577350269, 0.577350269, -0.577350269),
        float3(-0.577350269, -0.577350269, 0.577350269),
        float3(-0.577350269, 0.577350269, -0.577350269),
        float3(0.577350269, -0.577350269, -0.577350269),
        float3(-0.577350269, -0.577350269, -0.577350269)
    };
    uint closeHits = 0u;
    float closestHit = radius;
    nearestDirection = float3(0.0, 1.0, 0.0);
    for (uint directionIndex = 0u; directionIndex < 14u; ++directionIndex)
    {
        RayDesc ray;
        ray.Origin = position;
        ray.Direction = directions[directionIndex];
        ray.TMin = 0.001;
        ray.TMax = radius;
        RayQuery<RAY_FLAG_FORCE_OPAQUE> query;
        query.TraceRayInline(sceneTlas, RAY_FLAG_FORCE_OPAQUE, 0xFF, ray);
        while (query.Proceed())
        {
        }
        if (query.CommittedStatus() == COMMITTED_TRIANGLE_HIT &&
            query.CommittedRayT() < radius)
        {
            float hitDistance = query.CommittedRayT();
            if (hitDistance < closestHit)
                nearestDirection = directions[directionIndex];
            closestHit = min(closestHit, hitDistance);
            if (hitDistance < epsilon)
                ++closeHits;
        }
    }
    freeSpace = closeHits < 2u;
    return closestHit;
}

bool OverlapsMeshPartBounds(
    float3 worldPosition,
    float padding)
{
    if (push.instanceCount == 0u ||
        push.partCount == 0u)
    {
        return false;
    }

    for (uint instanceIndex = 0u;
         instanceIndex < push.instanceCount;
         ++instanceIndex)
    {
        InstanceData instance = push.instances[instanceIndex];
        uint partEnd = min(
            instance.firstPartIndex + instance.partCount,
            push.partCount);
        for (uint partIndex = instance.firstPartIndex;
             partIndex < partEnd;
             ++partIndex)
        {
            PartData part = push.parts[partIndex];
            float3 boundsMin =
                part.aabbMin.xyz + part.localOffset.xyz;
            float3 boundsMax =
                part.aabbMax.xyz + part.localOffset.xyz;
            float3 worldBoundsMin = float3(1e30, 1e30, 1e30);
            float3 worldBoundsMax = float3(-1e30, -1e30, -1e30);
            for (uint cornerIndex = 0u;
                 cornerIndex < 8u;
                 ++cornerIndex)
            {
                float3 localCorner = float3(
                    (cornerIndex & 1u) == 0u
                        ? boundsMin.x : boundsMax.x,
                    (cornerIndex & 2u) == 0u
                        ? boundsMin.y : boundsMax.y,
                    (cornerIndex & 4u) == 0u
                        ? boundsMin.z : boundsMax.z);
                float3 worldCorner = mul(
                    instance.modelMatrix,
                    float4(localCorner, 1.0)).xyz;
                worldBoundsMin = min(worldBoundsMin, worldCorner);
                worldBoundsMax = max(worldBoundsMax, worldCorner);
            }
            if (all(worldPosition >= worldBoundsMin - padding) &&
                all(worldPosition <= worldBoundsMax + padding))
            {
                return true;
            }
        }
    }
    return false;
}

[shader("compute")]
[NumThreads(64, 1, 1)]
void computeMain(uint3 dispatchId : SV_DispatchThreadID)
{
    uint requestIndex = dispatchId.x;
    if (requestIndex >= push.requestCount)
        return;

    ProbeRequest request = push.probeRequests[requestIndex];
    uint slot = request.probeSlot;
    if (slot >= (uint)push.probeBudgetAndParams.x)
        return;

    uint resolution = (uint)push.volumeExtentAndGridResolution.w;
    uint cellsPerLevel = resolution * resolution * resolution;
    uint clipmapLevel = request.clipmapLevel;
    float levelScale = pow(
        push.probeBudgetAndParams.w,
        float(clipmapLevel));
    float3 cellSize =
        push.volumeExtentAndGridResolution.xyz * levelScale;
    float3 camera = push.cameraPositionAndFrame.xyz;
    float3 position = request.worldPosition.xyz;
    bool newAllocation = (request.flags & 1u) != 0u;
    bool sceneBake = (request.flags & 2u) != 0u;
    int3 worldCell = (int3)floor(position / cellSize);
    RegisterProbeKey(slot, worldCell, clipmapLevel);

    ProbeState previous;
    if (newAllocation)
    {
        previous.flags = 0u;
        previous.lastUpdateFrame = 0u;
        previous.geometryRevision = 0u;
        previous.lightRevision = 0u;
    }
    else
    {
        previous = push.probeStates[slot];
    }

    bool wasActive = (previous.flags & 1u) != 0u;
    bool wasReady = !newAllocation &&
        push.probePositions[slot].w > 0.5;
    bool pendingClassification = (previous.flags & 8u) != 0u;
    bool geometryChanged = newAllocation ||
        previous.geometryRevision != push.geometryRevision;
    bool needsClassification = geometryChanged || pendingClassification;
    uint classificationOrdinal = 0u;
    bool classifyNow = false;
    if (needsClassification)
    {
        if (sceneBake)
        {
            InterlockedAdd(
                push.probeCounter[3],
                1u,
                classificationOrdinal);
            classifyNow = classificationOrdinal <
                push.maxSceneBakeClassifications &&
                push.useSceneTlas != 0u;
        }
        else
        {
            InterlockedAdd(
                push.probeCounter[2],
                1u,
                classificationOrdinal);
            classifyNow = classificationOrdinal <
                push.maxRelocationClassifications &&
                push.useSceneTlas != 0u;
        }
    }

    float4 clip = mul(push.viewProjection, float4(position, 1.0));
    bool visible = clip.w > 0.0 &&
        abs(clip.x) <= clip.w &&
        abs(clip.y) <= clip.w &&
        clip.z >= 0.0 && clip.z <= clip.w;
    bool active = wasActive;
    bool relocated = (previous.flags & 16u) != 0u;
    bool boundsPriority = (previous.flags & 64u) != 0u;
    bool previousSkyOnly = (previous.flags & 128u) != 0u;
    bool skyOnly = previousSkyOnly;
    float3 placedPosition = wasActive
        ? push.probePositions[slot].xyz
        : position;
    if (classifyNow)
    {
        float cellRadius = length(cellSize) * 2.5;
        bool freeSpace;
        float3 nearestDirection;
        float geometryDistance = ClassifyGeometry(
            position,
            cellRadius,
            push.probeBudgetAndParams.z,
            freeSpace,
            nearestDirection);
        bool geometryNear = geometryDistance < cellRadius;
        bool overlapsMeshBounds = OverlapsMeshPartBounds(
            position,
            cellRadius * 0.5);
        boundsPriority = geometryNear && overlapsMeshBounds;
        skyOnly = !sceneBake && !geometryNear;
        active = push.useSceneTlas != 0u &&
            (geometryNear || skyOnly);
        placedPosition = position;
        relocated = false;
        float clearance = max(
            push.probeBudgetAndParams.y,
            min(min(cellSize.x, min(cellSize.y, cellSize.z)) * 0.35, 2.0));
        bool needsRelocation = active && !skyOnly &&
            ((overlapsMeshBounds &&
              geometryDistance < clearance * 2.0) ||
             !freeSpace ||
             geometryDistance < clearance);
        if (needsRelocation)
        {
            float awayDistance = max(
                clearance - geometryDistance,
                clearance);
            float throughDistance = geometryDistance + clearance;
            float3 awayPosition =
                position - nearestDirection * awayDistance;
            float3 throughPosition =
                position + nearestDirection * throughDistance;
            bool awayFreeSpace;
            float3 awayNearestDirection;
            float awayGeometryDistance = ClassifyGeometry(
                awayPosition,
                cellRadius,
                push.probeBudgetAndParams.z,
                awayFreeSpace,
                awayNearestDirection);
            bool awayValid = awayFreeSpace &&
                awayGeometryDistance < cellRadius &&
                awayGeometryDistance >= clearance * 0.75;
            bool throughFreeSpace;
            float3 throughNearestDirection;
            float throughGeometryDistance = ClassifyGeometry(
                throughPosition,
                cellRadius,
                push.probeBudgetAndParams.z,
                throughFreeSpace,
                throughNearestDirection);
            bool throughValid = throughFreeSpace &&
                throughGeometryDistance < cellRadius &&
                throughGeometryDistance >= clearance * 0.75;
            bool useAway = awayValid &&
                (!throughValid || awayDistance <= throughDistance);
            active = awayValid || throughValid;
            placedPosition = useAway
                ? awayPosition
                : throughPosition;
            relocated = active;
        }
        else if (!skyOnly)
        {
            active = active && freeSpace;
        }
    }
    else if (newAllocation)
    {
        active = false;
    }

    bool classificationPending = needsClassification && !classifyNow;
    bool needsUpdate = active && needsClassification && classifyNow &&
        (!skyOnly || !wasReady || !previousSkyOnly);
    bool retainedSkyHistory = classifyNow && skyOnly && previousSkyOnly;
    bool ready = active && wasReady &&
        (!needsClassification || retainedSkyHistory);
    uint flags = active ? 1u : 0u;
    flags |= visible ? 2u : 0u;
    flags |= needsUpdate ? 4u : 0u;
    flags |= classificationPending ? 8u : 0u;
    flags |= relocated ? 16u : 0u;
    flags |= sceneBake ? 32u : 0u;
    flags |= boundsPriority ? 64u : 0u;
    flags |= skyOnly ? 128u : 0u;
    flags |= (clipmapLevel & 3u) << 8u;
    if (!needsUpdate)
        flags |= previous.flags & (
            kRadianceConvergenceMask |
            kInitialRadianceAccumulation);
    ProbeState state;
    state.flags = flags;
    state.lastUpdateFrame = active
        ? previous.lastUpdateFrame
        : (uint)push.cameraPositionAndFrame.w;
    state.geometryRevision = classifyNow
        ? push.geometryRevision
        : previous.geometryRevision;
    state.lightRevision = needsUpdate
        ? 0u
        : previous.lightRevision;
    push.probeStates[slot] = state;
    push.probePositions[slot] = float4(
        placedPosition,
        ready ? 1.0 : 0.0);
    if (request.gridCellIndex != 0xFFFFFFFFu)
    {
        push.gridToProbeIndex[request.gridCellIndex] =
            active ? (int)slot : -1;
    }
    if (active)
        InterlockedAdd(push.probeCounter[0], 1u);

    if (request.gridCellIndex != 0xFFFFFFFFu)
    {
        uint localCellIndex = request.gridCellIndex % cellsPerLevel;
        if (request.gridCellIndex == 0u)
        {
            push.volumeState[0] = 0x44444749u;
            push.volumeState[1] = push.clipmapLevelCount;
            push.volumeState[2] = resolution;
            push.volumeState[3] = asuint(push.cameraPositionAndFrame.w);
        }
        if (localCellIndex == 0u)
        {
            float3 snappedCenter = floor(camera / cellSize) * cellSize;
            uint levelState = 4u + clipmapLevel * 8u;
            push.volumeState[levelState + 0u] = asuint(snappedCenter.x);
            push.volumeState[levelState + 1u] = asuint(snappedCenter.y);
            push.volumeState[levelState + 2u] = asuint(snappedCenter.z);
            push.volumeState[levelState + 3u] = asuint(cellSize.x);
            push.volumeState[levelState + 4u] = asuint(cellSize.y);
            push.volumeState[levelState + 5u] = asuint(cellSize.z);
            push.volumeState[levelState + 6u] = clipmapLevel * cellsPerLevel;
            push.volumeState[levelState + 7u] = cellsPerLevel;
        }
    }
}
