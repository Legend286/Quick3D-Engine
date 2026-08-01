// SPDX-License-Identifier: MIT

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

struct SchedulePushData
{
    float4 cameraPositionAndFrame;
    uint frameNumber;
    uint requestCount;
    uint persistentStart;
    uint persistentCount;
    uint allocatedProbeCount;
    uint maxUpdates;
    uint radianceRevision;
    uint gridResolution;
    uint clipmapLevelCount;
    uint padding0;
    ProbeRequest* probeRequests;
    float4* probePositions;
    ProbeState* probeStates;
    uint* probeCounter;
    uint* probeUpdateQueue;
};

[push_constant]
ConstantBuffer<SchedulePushData> push;

[shader("compute")]
[NumThreads(1, 1, 1)]
void computeMain()
{
    uint selected[128];
    float scores[128];
    uint count = min(push.maxUpdates, 128u);
    for (uint index = 0u; index < count; ++index)
    {
        selected[index] = 0xFFFFFFFFu;
        scores[index] = -1.0;
    }

    uint frame = push.frameNumber;
    uint candidateCount =
        push.requestCount + push.persistentCount;
    for (uint candidateIndex = 0u;
         candidateIndex < candidateCount;
         ++candidateIndex)
    {
        bool resident = candidateIndex < push.requestCount;
        uint probeIndex;
        bool sceneBake = false;
        if (resident)
        {
            ProbeRequest request =
                push.probeRequests[candidateIndex];
            probeIndex = request.probeSlot;
            sceneBake = (request.flags & 2u) != 0u;
            resident = request.gridCellIndex != 0xFFFFFFFFu;
        }
        else
        {
            if (push.allocatedProbeCount == 0u)
                continue;
            uint persistentOffset =
                candidateIndex - push.requestCount;
            probeIndex =
                (push.persistentStart + persistentOffset) %
                push.allocatedProbeCount;
        }
        ProbeState state = push.probeStates[probeIndex];
        sceneBake = sceneBake || (state.flags & 32u) != 0u;
        bool active = (state.flags & 1u) != 0u;
        bool pendingClassification = (state.flags & 8u) != 0u;
        if (!active || pendingClassification)
            continue;

        bool visible = (state.flags & 2u) != 0u;
        bool dirty = (state.flags & 4u) != 0u;
        bool boundsPriority = (state.flags & 64u) != 0u;
        bool skyOnly = (state.flags & 128u) != 0u;
        bool converging =
            (state.flags & kRadianceConvergenceMask) != 0u;
        bool ready = push.probePositions[probeIndex].w > 0.5;
        bool skyDirty =
            (state.lightRevision >> 16u) !=
            (push.radianceRevision >> 16u);
        bool lightingDirty = skyOnly
            ? skyDirty
            : state.lightRevision != push.radianceRevision;
        uint age = frame >= state.lastUpdateFrame
            ? frame - state.lastUpdateFrame
            : frame;
        float distanceToCamera = length(
            push.probePositions[probeIndex].xyz -
            push.cameraPositionAndFrame.xyz);
        uint level = min(
            (state.flags >> 8u) & 3u,
            max(push.clipmapLevelCount, 1u) - 1u);
        if (skyOnly && ready && !skyDirty && !converging)
            continue;
        if (ready && !dirty && !lightingDirty && !converging)
            continue;
        if ((lightingDirty || converging) && ready &&
            age < 2u && !dirty)
            continue;

        float fineLevelWeight = float(
            max(push.clipmapLevelCount, 1u) - level);
        float score = (!ready ? 50000.0 : 0.0) +
            (dirty ? 30000.0 : 0.0) +
            (sceneBake && !ready ? 20000.0 : 0.0) +
            (lightingDirty ? 10000.0 : 0.0) +
            (converging ? 8000.0 : 0.0) +
            (boundsPriority ? 2500.0 : 0.0) +
            (visible ? 1000.0 : 0.0) +
            (resident ? 500.0 : 0.0) +
            fineLevelWeight * 64.0 +
            128.0 / (1.0 + distanceToCamera);
        bool duplicate = false;
        for (uint existing = 0u; existing < count; ++existing)
        {
            if (selected[existing] == probeIndex)
            {
                duplicate = true;
                break;
            }
        }
        if (duplicate)
            continue;
        for (uint slot = 0u; slot < count; ++slot)
        {
            if (score <= scores[slot])
                continue;
            for (uint move = count - 1u; move > slot; --move)
            {
                scores[move] = scores[move - 1u];
                selected[move] = selected[move - 1u];
            }
            scores[slot] = score;
            selected[slot] = probeIndex;
            break;
        }
    }

    uint updateCount = 0u;
    for (uint index = 0u; index < count; ++index)
    {
        if (selected[index] == 0xFFFFFFFFu)
            break;
        push.probeUpdateQueue[updateCount++] = selected[index];
    }
    push.probeCounter[1] = updateCount;
}
