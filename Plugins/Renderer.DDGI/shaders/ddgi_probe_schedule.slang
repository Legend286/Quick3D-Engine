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
static const uint kSchedulerThreadCount = 128u;
static const uint kCandidatesPerLane = 2u;
static const uint kReducedCandidateCount =
    kSchedulerThreadCount * kCandidatesPerLane;

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

groupshared uint g_candidateProbes[kReducedCandidateCount];
groupshared float g_candidateScores[kReducedCandidateCount];

bool ScoreCandidate(
    uint candidateIndex,
    out uint probeIndex,
    out float score)
{
    bool resident = candidateIndex < push.requestCount;
    bool sceneBake = false;
    if (resident)
    {
        ProbeRequest request = push.probeRequests[candidateIndex];
        probeIndex = request.probeSlot;
        sceneBake = (request.flags & 2u) != 0u;
        resident = request.gridCellIndex != 0xffffffffu;
    }
    else
    {
        if (push.allocatedProbeCount == 0u)
        {
            probeIndex = 0xffffffffu;
            score = -1.0;
            return false;
        }
        uint persistentOffset = candidateIndex - push.requestCount;
        probeIndex =
            (push.persistentStart + persistentOffset) %
            push.allocatedProbeCount;
    }

    ProbeState state = push.probeStates[probeIndex];
    sceneBake = sceneBake || (state.flags & 32u) != 0u;
    bool active = (state.flags & 1u) != 0u;
    bool pendingClassification = (state.flags & 8u) != 0u;
    if (!active || pendingClassification)
    {
        score = -1.0;
        return false;
    }

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
    uint frame = push.frameNumber;
    uint age = frame >= state.lastUpdateFrame
        ? frame - state.lastUpdateFrame
        : frame;
    if (skyOnly && ready && !skyDirty && !converging)
    {
        score = -1.0;
        return false;
    }
    if (ready && !dirty && !lightingDirty && !converging)
    {
        score = -1.0;
        return false;
    }
    if ((lightingDirty || converging) && ready &&
        age < 2u && !dirty)
    {
        score = -1.0;
        return false;
    }

    float distanceToCamera = length(
        push.probePositions[probeIndex].xyz -
        push.cameraPositionAndFrame.xyz);
    uint level = min(
        (state.flags >> 8u) & 3u,
        max(push.clipmapLevelCount, 1u) - 1u);
    float fineLevelWeight = float(
        max(push.clipmapLevelCount, 1u) - level);
    score = (!ready ? 50000.0 : 0.0) +
        (dirty ? 30000.0 : 0.0) +
        (sceneBake && !ready ? 20000.0 : 0.0) +
        (lightingDirty ? 10000.0 : 0.0) +
        (converging ? 8000.0 : 0.0) +
        (boundsPriority ? 2500.0 : 0.0) +
        (visible ? 1000.0 : 0.0) +
        (resident ? 500.0 : 0.0) +
        fineLevelWeight * 64.0 +
        128.0 / (1.0 + distanceToCamera);
    return true;
}

void InsertLaneCandidate(
    uint probeIndex,
    float score,
    inout uint firstProbe,
    inout float firstScore,
    inout uint secondProbe,
    inout float secondScore)
{
    if (probeIndex == firstProbe)
    {
        firstScore = max(firstScore, score);
        return;
    }
    if (probeIndex == secondProbe)
    {
        secondScore = max(secondScore, score);
        if (secondScore > firstScore)
        {
            uint swappedProbe = firstProbe;
            float swappedScore = firstScore;
            firstProbe = secondProbe;
            firstScore = secondScore;
            secondProbe = swappedProbe;
            secondScore = swappedScore;
        }
        return;
    }
    if (score > firstScore)
    {
        secondProbe = firstProbe;
        secondScore = firstScore;
        firstProbe = probeIndex;
        firstScore = score;
    }
    else if (score > secondScore)
    {
        secondProbe = probeIndex;
        secondScore = score;
    }
}

[shader("compute")]
[NumThreads(128, 1, 1)]
void computeMain(uint3 groupThreadID : SV_GroupThreadID)
{
    uint lane = groupThreadID.x;
    if (push.maxUpdates == 0u)
    {
        if (lane == 0u)
            push.probeCounter[1] = 0u;
        return;
    }

    uint firstProbe = 0xffffffffu;
    uint secondProbe = 0xffffffffu;
    float firstScore = -1.0;
    float secondScore = -1.0;
    uint candidateCount = push.requestCount + push.persistentCount;
    for (uint candidateIndex = lane;
         candidateIndex < candidateCount;
         candidateIndex += kSchedulerThreadCount)
    {
        uint probeIndex;
        float score;
        if (!ScoreCandidate(candidateIndex, probeIndex, score))
            continue;
        InsertLaneCandidate(
            probeIndex,
            score,
            firstProbe,
            firstScore,
            secondProbe,
            secondScore);
    }

    uint reducedIndex = lane * kCandidatesPerLane;
    g_candidateProbes[reducedIndex] = firstProbe;
    g_candidateScores[reducedIndex] = firstScore;
    g_candidateProbes[reducedIndex + 1u] = secondProbe;
    g_candidateScores[reducedIndex + 1u] = secondScore;
    GroupMemoryBarrierWithGroupSync();

    if (lane != 0u)
        return;

    uint updateLimit = min(push.maxUpdates, 128u);
    uint updateCount = 0u;
    for (uint outputIndex = 0u;
         outputIndex < updateLimit;
         ++outputIndex)
    {
        uint bestCandidate = 0xffffffffu;
        float bestScore = -1.0;
        for (uint candidateIndex = 0u;
             candidateIndex < kReducedCandidateCount;
             ++candidateIndex)
        {
            if (g_candidateScores[candidateIndex] > bestScore)
            {
                bestScore = g_candidateScores[candidateIndex];
                bestCandidate = candidateIndex;
            }
        }
        if (bestCandidate == 0xffffffffu)
            break;

        uint selectedProbe = g_candidateProbes[bestCandidate];
        push.probeUpdateQueue[updateCount++] = selectedProbe;
        for (uint candidateIndex = 0u;
             candidateIndex < kReducedCandidateCount;
             ++candidateIndex)
        {
            if (g_candidateProbes[candidateIndex] == selectedProbe)
                g_candidateScores[candidateIndex] = -1.0;
        }
    }
    push.probeCounter[1] = updateCount;
}
