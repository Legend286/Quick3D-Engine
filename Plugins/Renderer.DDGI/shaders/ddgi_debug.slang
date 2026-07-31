// SPDX-License-Identifier: MIT
// DDGI v1 probe visualisation. Per-vertex colour derives from the
// canonical two-band SH reconstruction the consumer (ddgi_sampling.slang)
// uses for indirect diffuse, so the marker shows the actual probe
// contribution at each face direction. Atlas slots that haven't been
// populated yet (compute pass hasn't run, slot zero, or SH energy
// below the noise floor) render safety orange so the user can SEE
// where probes WILL go before the update kernel has fired.

struct DDGIDebugPush
{
    float4x4 ViewProj;
    uint     ProbeCount;
    float    HalfSize;
    float    OriginY;
    float    HalfHeight;
    uint     IrradianceBindlessIndex;
    uint     Pad;
};

// MARK: Sentinel slot. Mirrors RhiBindlessHeap.InvalidSlot
// (= uint.MaxValue) and ddgi_sampling.slang::kInvalidSlot. Used
// here so a DDGI atlas legitimately bound at bindless slot 0 is
// not mis-classified as "unbound" by the previous `== 0u` check.
// Slot 0 is a valid heap slot (next_unalloc starts at 0); the
// sentinel must be a value the allocator never returns, hence
// uint.MaxValue.
static const uint kInvalidSlot = 0xFFFFFFFFu;

// MARK: Push-constant cbuffer written by DDGIDebugPass.Execute
// via sink.PushConstants(0, sizeof(DebugPush), ...). Must use
// [push_constant] (matching pbr.slang's ScenePushData convention)
// so the sink calls bind to this cbuffer rather than to a regular
// descriptor-set binding whose slot would alias the bindless heap.
struct BindlessHeap
{
    Texture2D textures[4096];
};

[[vk::binding(1, 0)]] ParameterBlock<BindlessHeap> bindless;

[push_constant]
ConstantBuffer<DDGIDebugPush> Push;

StructuredBuffer<float4> Probes : register(t5);

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

// MARK: Canonical two-band SH eval - must match the consumer
// (ddgi_sampling.slang) byte-for-byte so the debug viz reflects the
// exact lighting contribution a PBR fragment would receive from the
// same probe. Coefficients carry the SH A.L constant pre-multiplied.
float3 EvaluateTwoBandSH(
    float4 c0, float4 c1, float4 c2, float4 c3, float3 N)
{
    float3 L0 = c0.rgb;
    float3 L1 = N.y * c1.rgb
              + N.z * c2.rgb
              + N.x * c3.rgb;
    return max(L0 + L1, float3(0.0));
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
    uint probeIdx = vid / 24u;
    uint vertIdx  = vid % 24u;

    if (probeIdx >= Push.ProbeCount)
    {
        o.position = float4(2.0, 2.0, 2.0, 1.0);
        o.color    = float3(0.0);
        return o;
    }

    float3 center = Probes[probeIdx].xyz;

    float3 localPos = kOctaVerts[kOctaIdx[vertIdx]];
    float3 worldPos = center + localPos * Push.HalfSize;
    o.position = mul(Push.ViewProj, float4(worldPos, 1.0));

    uint irradSlot = Push.IrradianceBindlessIndex;
    int  colBase   = (int)(probeIdx * 4);
    float3 irradiance = float3(0.0);
    float energy = 0.0;

    if (irradSlot != kInvalidSlot)
    {
        float4 c0 = bindless.textures[irradSlot].Load(int3(colBase + 0, 0, 0));
        float4 c1 = bindless.textures[irradSlot].Load(int3(colBase + 1, 0, 0));
        float4 c2 = bindless.textures[irradSlot].Load(int3(colBase + 2, 0, 0));
        float4 c3 = bindless.textures[irradSlot].Load(int3(colBase + 3, 0, 0));

        float3 N = normalize(localPos);
        irradiance = EvaluateTwoBandSH(c0, c1, c2, c3, N);
        energy = irradiance.x + irradiance.y + irradiance.z;
    }

    // MARK: Status fall-back. An unpopulated slot reads as
    // near-zero SH energy; surface those markers explicitly so the
    // spatial grid stays visible before any probe has been updated.
    // The `irradSlot == kInvalidSlot` branch fires only when the
    // C# producer explicitly wrote InvalidSlot (0xFFFFFFFF) due to
    // a null shared heap, since the native allocator never returns
    // that value naturally.
    bool atlasUnbound = (irradSlot == kInvalidSlot);
    bool atlasEmpty = (energy < 1.0e-4);
    if (atlasUnbound || atlasEmpty)
    {
        // Spatial fallback: colour unpopulated probes by their
        // world-space Y gradient (cold blue at the volume bottom,
        // warm cream at the top) so every probe in frustum stays
        // visible before the probe-update kernel has populated the
        // atlas. OriginY + HalfHeight are copied through the push
        // cbuffer from C# so the lerp normalises correctly against
        // any volume the plugin constructs at scene-load.
        float yNorm =
            saturate(
                (center.y - Push.OriginY + Push.HalfHeight) /
                    (2.0 * Push.HalfHeight));
        o.color = lerp(
            float3(0.10, 0.05, 0.30),
            float3(0.95, 0.85, 0.65),
            yNorm);
    }
    else
    {
        o.color = irradiance + float3(0.04, 0.04, 0.04);
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
