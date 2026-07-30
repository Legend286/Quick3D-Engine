// SPDX-License-Identifier: MIT
// DDGI indirect-diffuse reconstruction. Lives behind
// `#ifdef DDGI_PLUGIN` in `Content/shaders/pbr.slang` and only takes
// over `SampleIndirectDiffuse` when the DDGI plugin is enabled.
//
// For each ray-hit, the probe atlas stores 4 RGB SH2 coefficients
// (Y00, Y1_-1, Y10, Y11) packed as 4 float4 lanes. At sample time
// the host shoves one float4 per probe into a small cache and we
// trilinearly blend the eight corner probes and project the result
// onto the shading normal.

struct DDGISH2CoefficientSet
{
    float4 c0; // Y00   (constant lobe)
    float4 c1; // Y1_-1 (z lobe)
    float4 c2; // Y10   (y lobe)
    float4 c3; // Y11   (x lobe)
};

static const float kDDGIMaxIrradiance = 32.0;

float3 DDGIReconstructSH2(
    DDGISH2CoefficientSet sh,
    float3 n)
{
    float3 result =
        sh.c0.rgb +
        sh.c1.rgb * n.z +
        sh.c2.rgb * n.y +
        sh.c3.rgb * n.x;
    return max(result, float3(0.0, 0.0, 0.0));
}

float3 DDGIBlendProbes(
    DDGISH2CoefficientSet probes[8],
    float3 blendWeights,
    float3 shadingNormal)
{
    float3 sumIrradiance = float3(0.0, 0.0, 0.0);
    float weightSum = 0.0;
    float3 safeNormal = normalize(shadingNormal + float3(0.0001, 0.0001, 0.0001));
    for (int i = 0; i < 8; ++i)
    {
        float w = max(blendWeights[i], 0.0);
        sumIrradiance += w * DDGIReconstructSH2(probes[i], safeNormal);
        weightSum += w;
    }
    if (weightSum <= 0.0)
        return float3(0.0, 0.0, 0.0);
    return clamp(
        sumIrradiance / weightSum,
        float3(0.0, 0.0, 0.0),
        float3(kDDGIMaxIrradiance, kDDGIMaxIrradiance, kDDGIMaxIrradiance));
}

float3 SampleIndirectDiffuse(float3 worldPosition, float3 worldNormal)
{
    // Default fallback when the host did not push a probe-atlas
    // uniform set (probe volume still bound but SH2 not yet filled
    // because the dispatcher has not run). Returns a small upward
    // sky term so the host PBR pass keeps working. The full
    // DDGIBlendProbes() call awaits Phase 3's atlas upload path.
    if (worldNormal.y > 0.0)
        return float3(0.05 + 0.4 * worldNormal.y, 0.06 + 0.05 * worldNormal.y, 0.10);
    return float3(0.02, 0.02, 0.04);
}
