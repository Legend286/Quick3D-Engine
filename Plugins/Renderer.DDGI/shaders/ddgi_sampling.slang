// SPDX-License-Identifier: MIT
// DDGI v1 indirect-diffuse sampling for pbr.slang. Pulled in via
// `#include "ddgi_sampling.slang"` when DDGI_PLUGIN is defined.
//
// Atlas layout (see Plugins/Renderer.DDGI/DDGIAtlasResources.cs):
//   * irradiance[probeCount * 4, 1] RGBA16F — 4 components per probe
//     hold L00, L1_-1, L10, L11 (already pre-multiplied with
//     A.L constants per the canonical SH conv).
//   * visibility[probeCount, 1] RGBA16F — r = mean hit distance,
//     g = mean-of-squares (Chebychev moment 2).
//   * probePositions[probeCount] float3 — sparse world-space probe
//     centres. Sampled to recover a probe's true position from the
//     coarse-grid indirection table.
//   * gridToProbeIndex[coarseX*coarseY*coarseZ] int — coarse-grid
//     cell -> sparse probe index in ProbePositions. -1 sentinel
//     marks empty cells.
//
// Shader binding slots are reserved across all DDGI
// consumers. PbrPass calls UseBuffer(probePositions) +
// UseBuffer(gridToProbeIndex) on every frame the plugin is
// active; the placement pass binds the same SSBOs at the same
// slots during its dispatch.

// MARK: Atlas + sparse-buffer bindings (consumer pass owns these).
// These are now accessed via push.DDGIProbePositions and push.DDGIGridToProbeIndex

// MARK: Atlas slot fetch.
// `kInvalidSlot` mirrors the C# producer's sentinel
// (engine_cs/Engine.RHI::RhiBindlessHeap.InvalidSlot == uint.MaxValue);
// producer writes this value into push.DDGIAtlasParams.xy when the
// shared heap wasn't available at atlas allocation time. The
// previous `if (irradSlot == 0)` sentinel collided with the
// heap's natural first-slot assignment, so a valid atlas bound at
// slot 0 was wrongly classified as "unbound" and the shader fell
// through to `return float3(0.0)` while the seeded consumers
// proceeded silently.
static const uint kInvalidSlot = 0xFFFFFFFFu;
uint FetchIrradianceSlot()
{
    return (uint)push.DDGIAtlasParams.x;
}
uint FetchVisibilitySlot()
{
    return (uint)push.DDGIAtlasParams.y;
}
bool SparseLayoutReady()
{
    return push.DDGIAtlasParams.w > 0.5;
}
bool SlotIsValid(uint slot)
{
    return slot != kInvalidSlot;
}
// MARK: Probe-grid mapping helpers
// Coarse-grid resolution is the per-axis count of the outer bounding
// grid; the atlas may pack fewer sparse probes than coarse cells.
float3 ResolveGridSpace(float3 worldPos)
{
    float3 origin = push.DDGIOriginAndCountZ.xyz;
    float3 halfExtent = push.DDGIExtentAndFlags.xyz;
    float gridCount = max((float)push.DDGIAtlasParams.z, 1.0);
    float3 cellSize = (halfExtent * 2.0) / gridCount;
    float3 gridSpace = ((worldPos - origin) / cellSize) + gridCount * 0.5;
    return gridSpace;
}

uint ProbeLinearIndex(int3 cell, uint gridCount)
{
    return (uint)(cell.z * (int)gridCount * (int)gridCount
                 + cell.y * (int)gridCount
                 + cell.x);
}

float3 ResolveProbeWorldPos(int3 cell, uint gridCount)
{
    float3 origin = push.DDGIOriginAndCountZ.xyz;
    float3 halfExtent = push.DDGIExtentAndFlags.xyz;
    float3 cellSize = (halfExtent * 2.0) / max((float)gridCount, 1.0);
    float3 cellCenter = ((float3)cell + 0.5) * cellSize;
    return origin + cellCenter - halfExtent;
}

// MARK: Sparse indirection
int LookupSparseProbeIndex(int3 cell, uint gridCount)
{
    uint linear = ProbeLinearIndex(cell, gridCount);
    return push.DDGIGridToProbeIndex[linear];
}

float3 LookupProbeWorldPos(int sparseIdx)
{
    if (sparseIdx < 0)
        return float3(0.0);
    return push.DDGIProbePositions[sparseIdx].xyz;
}

// MARK: SH eval
float3 EvaluateTwoBandSH(
    float4 c0,
    float4 c1,
    float4 c2,
    float4 c3,
    float3 N)
{
    // L00 = c0; L1_-1 = c1 (y in some conventions), L10 = c2 (z),
    // L11 = c3 (x). Mapping follows the canonical SH basis-Z
    // orientation. Coefficients carry the A.L conv pre-multiplied.
    float3 L0 = c0.rgb;
    float3 L1 = N.y * c1.rgb
              + N.z * c2.rgb
              + N.x * c3.rgb;
    return max(L0 + L1, float3(0.0));
}

// MARK: Visibility Chebychev
float VisibilityWeight(float mean, float meanSq, float distanceToProbe)
{
    if (distanceToProbe <= mean)
        return 1.0;
    float variance = max(meanSq - mean * mean, 0.0001);
    float d = distanceToProbe - mean;
    float chebychev = variance / (variance + d * d);
    return smoothstep(0.05, 1.0, chebychev);
}

// MARK: Entry point consumed by pbr.slang
float3 SampleIndirectDiffuse(float3 worldPosition, float3 worldNormal)
{
    if (!SparseLayoutReady())
        return float3(0.0);

    uint irradSlot = FetchIrradianceSlot();
    if (!SlotIsValid(irradSlot))
        return float3(0.0);

    uint visSlot = FetchVisibilitySlot();
    if (!SlotIsValid(visSlot))
        return float3(0.0);
    Texture2D irradTex = bindless.textures[irradSlot];
    Texture2D visTex = bindless.textures[visSlot];

    uint gridCount = (uint)max(push.DDGIAtlasParams.z, 1.0);

    float3 gridSpace = ResolveGridSpace(worldPosition);
    int3 baseCell = (int3)floor(gridSpace);
    float3 alpha = saturate(gridSpace - (float3)baseCell);

    float3 sumIrradiance = float3(0.0);
    float sumWeight = 0.0;

    [unroll]
    for (int oz = 0; oz < 2; ++oz)
    [unroll]
    for (int oy = 0; oy < 2; ++oy)
    [unroll]
    for (int ox = 0; ox < 2; ++ox)
    {
        int3 cell = baseCell + int3(ox, oy, oz);
        if (any(cell < int3(0, 0, 0)) ||
            any(cell >= int3((int)gridCount, (int)gridCount, (int)gridCount)))
            continue;

        // MARK: Sparse indirection lookup
        int sparseIdx = LookupSparseProbeIndex(cell, gridCount);
        if (sparseIdx < 0)
            continue;

        // 4 contiguous texels along column axis carry the SH coeffs.
        int colBase = (int)(sparseIdx * 4);
        float4 c0 = irradTex.Load(int3(colBase + 0, 0, 0));
        float4 c1 = irradTex.Load(int3(colBase + 1, 0, 0));
        float4 c2 = irradTex.Load(int3(colBase + 2, 0, 0));
        float4 c3 = irradTex.Load(int3(colBase + 3, 0, 0));
        float3 probeIrradiance = EvaluateTwoBandSH(c0, c1, c2, c3, worldNormal);

        if (any(isnan(probeIrradiance)) || any(isinf(probeIrradiance)))
            continue;

        float2 moments = visTex.Load(int3(sparseIdx, 0, 0)).rg;

        float3 probePos = LookupProbeWorldPos(sparseIdx);
        float distanceToProbe = length(probePos - worldPosition);
        float visWeight = VisibilityWeight(moments.x, moments.y, distanceToProbe);

        float trilinearWeight =
            lerp(1.0 - alpha.x, alpha.x, (float)ox) *
            lerp(1.0 - alpha.y, alpha.y, (float)oy) *
            lerp(1.0 - alpha.z, alpha.z, (float)oz);

        // Distance falloff — quadratic is canonical DDGI weighting.
        float distanceWeight = 1.0 /
            (1.0 + distanceToProbe * distanceToProbe * 0.05);

        float w = trilinearWeight * distanceWeight * visWeight;
        sumIrradiance += probeIrradiance * w;
        sumWeight += w;
    }

    if (sumWeight > 0.0)
        return sumIrradiance / sumWeight;
    return float3(0.0);
}
