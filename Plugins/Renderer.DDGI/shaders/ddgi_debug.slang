// SPDX-License-Identifier: MIT
struct ProbeRequest
{
    float4 worldPosition;
    uint probeSlot;
    uint gridCellIndex;
    uint clipmapLevel;
    uint flags;
};

struct DDGIDebugPush
{
    float4x4 ViewProj;
    uint     RequestCount;
    float    HalfSize;
    uint     ClipmapLevelCount;
    uint     ShowStatusColors;
    uint     IrradianceBindlessIndex;
    uint     Padding0;
    ProbeRequest* ProbeRequests;
    float4*  ProbePositions;
    uint4*   ProbeStates;
    int*     GridToProbeIndex;
};

struct BindlessHeap
{
    Texture2D textures[4096];
};

[[vk::binding(1, 0)]] ParameterBlock<BindlessHeap> bindless;

// MARK: Push-constant cbuffer written by DDGIDebugPass.Execute.
// The overlay intentionally has no atlas-texture dependency.
[push_constant]
ConstantBuffer<DDGIDebugPush> Push;

// MARK: Octahedron primitives indexed by 24 verts / 8 triangles.
static const float3 kOctaVerts[6] = {
    float3( 1.0, 0.0, 0.0),
    float3(-1.0, 0.0, 0.0),
    float3( 0.0, 1.0, 0.0),
    float3( 0.0,-1.0, 0.0),
    float3( 0.0, 0.0, 1.0),
    float3( 0.0, 0.0,-1.0),
};

static const int kOctaIdx[24] = {
    0, 2, 4,
    0, 4, 3,
    0, 3, 5,
    0, 5, 2,
    1, 4, 2,
    1, 3, 4,
    1, 5, 3,
    1, 2, 5,
};

float3 EvaluateTwoBandSH(
    float4 c0,
    float4 c1,
    float4 c2,
    float4 c3,
    float3 direction)
{
    return max(
        c0.rgb +
        direction.y * c1.rgb +
        direction.z * c2.rgb +
        direction.x * c3.rgb,
        float3(0.0));
}

int2 IrradianceCoordinate(uint probeIndex, uint coefficientIndex)
{
    uint linearIndex = probeIndex * 4u + coefficientIndex;
    return int2(linearIndex % 4096u, linearIndex / 4096u);
}

struct VertexOutput
{
    float4 position : SV_POSITION;
    float3 color    : COLOR0;
};

[shader("vertex")]
VertexOutput vsMain(uint vid : SV_VertexID)
{
    VertexOutput o;
    uint requestIdx = vid / 24u;
    uint vertIdx  = vid % 24u;

    if (requestIdx >= Push.RequestCount)
    {
        o.position = float4(2.0, 2.0, 2.0, 1.0);
        o.color    = float3(0.0);
        return o;
    }

    ProbeRequest request = Push.ProbeRequests[requestIdx];
    uint probeIdx = request.probeSlot;
    uint flags = Push.ProbeStates[probeIdx].x;
    bool active = (flags & 1u) != 0u;
    bool ready = Push.ProbePositions[probeIdx].w > 0.5;
    float3 center = active
        ? Push.ProbePositions[probeIdx].xyz
        : request.worldPosition.xyz;

    float3 localPos = kOctaVerts[kOctaIdx[vertIdx]];
    float3 worldPos = center + localPos * Push.HalfSize;
    o.position = mul(Push.ViewProj, float4(worldPos, 1.0));

    bool dirty = (flags & 4u) != 0u;
    bool pending = (flags & 8u) != 0u;
    bool converging = (flags & (7u << 10u)) != 0u;
    bool resident = request.gridCellIndex != 0xFFFFFFFFu &&
        Push.GridToProbeIndex[request.gridCellIndex] == (int)probeIdx;
    uint clipmapLevel = min(
        request.clipmapLevel,
        max(Push.ClipmapLevelCount, 1u) - 1u);
    float levelBrightness = clipmapLevel == 0u
        ? 1.0
        : clipmapLevel == 1u
            ? 0.82
            : 0.68;
    if (Push.ShowStatusColors != 0u)
    {
        o.color = pending
            ? float3(1.0, 0.30, 0.08)
            : !active
                ? float3(0.12, 0.12, 0.14)
            : !ready
                ? float3(1.0, 0.05, 0.02)
            : dirty || converging
                ? float3(1.0, 0.55, 0.08)
            : resident
                ? float3(0.05, 1.0, 0.12)
                : float3(0.35, 0.38, 0.42);
        o.color *= levelBrightness;
    }
    else if (ready && Push.IrradianceBindlessIndex != 0xFFFFFFFFu)
    {
        float4 c0 = bindless.textures[Push.IrradianceBindlessIndex].Load(
            int3(IrradianceCoordinate(probeIdx, 0u), 0));
        float4 c1 = bindless.textures[Push.IrradianceBindlessIndex].Load(
            int3(IrradianceCoordinate(probeIdx, 1u), 0));
        float4 c2 = bindless.textures[Push.IrradianceBindlessIndex].Load(
            int3(IrradianceCoordinate(probeIdx, 2u), 0));
        float4 c3 = bindless.textures[Push.IrradianceBindlessIndex].Load(
            int3(IrradianceCoordinate(probeIdx, 3u), 0));
        o.color = EvaluateTwoBandSH(
            c0,
            c1,
            c2,
            c3,
            normalize(localPos));
    }
    else
    {
        o.color = float3(0.02);
    }

    return o;
}

[shader("fragment")]
float4 fsMain(in VertexOutput input) : SV_Target
{
    float3 c = pow(max(input.color, float3(0.0)),
                   float3(1.0 / 2.2, 1.0 / 2.2, 1.0 / 2.2));
    return float4(c, 1.0);
}
