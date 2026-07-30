// SPDX-License-Identifier: MIT
// DDGI probe visualization shader.
// Renders each probe grid center as a small world-space quad. Topology
// is TriangleList with 4 vertices per probe; the host C# pass issues
// a single Draw(probeCount * 4, 1) call. Depth test is disabled at the
// pipeline level so the marker's read is invariant under geometry
// occlusion - the editor's intent is to inspect probe placement, not
// produce nicely z-sorted viz.
//
// Phase-2 limit: only probe positions are rendered. SH2 principal-
// lobe arrows ship when the SH2 atlas upload lands in commit 3.

struct DDGIDebugPush
{
    float4x4 ViewProj;
    uint ProbeCount;
    float HalfSize;
    float ExtentY;
};

StructuredBuffer<float3> Probes;
ConstantBuffer<DDGIDebugPush> Push;

struct VertexOutput
{
    float4 position : SV_POSITION;
    float3 color : COLOR0;
};

VertexOutput vsMain(uint vid : SV_VertexID)
{
    uint probeIdx = vid / 4;
    uint cornerIdx = vid & 3;

    VertexOutput o;
    if (probeIdx >= Push.ProbeCount)
    {
        o.position = float4(2.0, 2.0, 2.0, 1.0);
        o.color = float3(0.0);
        return o;
    }

    float halfSize = Push.HalfSize;
    float2 corner = float2(
        (cornerIdx == 1u || cornerIdx == 2u) ? halfSize : -halfSize,
        (cornerIdx == 2u || cornerIdx == 3u) ? halfSize : -halfSize);

    float3 center = Probes[probeIdx];
    float3 worldPos = center + float3(corner, 0.0);

    o.position = mul(Push.ViewProj, float4(worldPos, 1.0));

    // Probe-Y normalization: center.y ranges over approximately
    // +/- ExtentY, so `2.0 * |y| / ExtentY` clamps to ~0..1 across
    // the volume's vertical span regardless of how ExtentY was
    // chosen (Phase 2 picked 16m; the doc says volumes of any size).
    float normalized = 2.0 * abs(center.y) / max(Push.ExtentY, 0.0001);
    float baseColor = 0.55 + 0.45 * saturate(normalized);
    o.color = float3(0.30 * baseColor, 1.00 * baseColor, 0.45 * baseColor);
    return o;
}

float4 fsMain(in VertexOutput input) : SV_Target
{
    return float4(input.color, 1.0);
}
