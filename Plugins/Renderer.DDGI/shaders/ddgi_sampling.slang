// SPDX-License-Identifier: MIT
// DDGI v1 indirect-diffuse sampling for pbr.slang. Pulled in via
// `#include "ddgi_sampling.slang"` when DDGI_PLUGIN is defined.
//
// Atlas layout (see Plugins/Renderer.DDGI/DDGIAtlasResources.cs):
//   * irradiance[probeCount * 4, 1] RGBA16F — 4 components per probe
//     hold L00, L1_-1, L10, L11 (already pre-multiplied with
//     A.L constants per the canonical SH conv).
//   * visibility[probeCount * 16, 1] RGBA16F — 4x4 octahedral
//     distance-moment tiles with mean in r and mean-of-squares in g.
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
static const uint kVisibilityTileResolution = 4u;
static const uint kVisibilityTexelsPerProbe = 16u;
static const uint kInitialRadianceAccumulation = 1u << 13u;
static const uint kSpecularTileResolution = 4u;
static const uint kSpecularRoughnessLevels = 4u;
static const uint kSpecularTexelsPerLevel = 16u;
static const uint kSpecularTexelsPerProbe = 64u;
static const uint kSpecularValid = 1u;

uint FetchIrradianceSlot()
{
    return (uint)push.DDGIAtlasParams.x;
}
uint FetchVisibilitySlot()
{
    return (uint)push.DDGIAtlasParams.y;
}
uint FetchSpecularSlot()
{
    return (uint)push.DDGISpecularParams.x;
}
bool SparseLayoutReady()
{
    return push.DDGIAtlasParams.w > 0.5;
}
bool SlotIsValid(uint slot)
{
    return slot != kInvalidSlot;
}

uint2 AtlasCoordinate(uint linearIndex)
{
    static const uint atlasWidth = 4096u;
    return uint2(
        linearIndex % atlasWidth,
        linearIndex / atlasWidth);
}
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

int LookupPersistentProbe(int3 worldCell, uint level)
{
    uint hashCapacity = (uint)push.DDGIExtentAndFlags.w;
    if (hashCapacity == 0u)
        return -1;
    uint hashIndex = HashProbeKey(worldCell, level) &
        (hashCapacity - 1u);
    for (uint attempt = 0u; attempt < 128u; ++attempt)
    {
        uint entry = push.DDGIWorldProbeHash[hashIndex];
        if (entry == 0u)
            return -1;
        uint probeIndex = entry - 1u;
        int4 key = push.DDGIProbeWorldKeys[probeIndex];
        if (all(key.xyz == worldCell) && key.w == (int)level)
            return (int)probeIndex;
        hashIndex = (hashIndex + 1u) & (hashCapacity - 1u);
    }
    return -1;
}

float3 LookupProbeWorldPos(int sparseIdx)
{
    if (sparseIdx < 0)
        return float3(0.0);
    return push.DDGIProbePositions[sparseIdx].xyz;
}

bool ProbeIsReady(int sparseIdx)
{
    return sparseIdx >= 0 &&
        push.DDGIProbePositions[sparseIdx].w > 0.5 &&
        (push.DDGIProbeStates[sparseIdx].x &
            kInitialRadianceAccumulation) == 0u;
}

bool ProbeSpecularIsReady(int sparseIdx)
{
    if (!ProbeIsReady(sparseIdx))
        return false;
    return (push.DDGIProbeSpecularStates[sparseIdx].z &
        kSpecularValid) != 0u;
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
    if (distanceToProbe <= mean + 0.25)
        return 1.0;
    float variance = max(meanSq - mean * mean, 0.01);
    float d = distanceToProbe - mean;
    float chebychev = variance / (variance + d * d);
    return max(0.02, chebychev);
}

float2 OctahedralEncode(float3 direction)
{
    direction /= max(
        abs(direction.x) + abs(direction.y) + abs(direction.z),
        0.0001);
    if (direction.z < 0.0)
    {
        float2 signValue = float2(
            direction.x >= 0.0 ? 1.0 : -1.0,
            direction.y >= 0.0 ? 1.0 : -1.0);
        direction.xy =
            (1.0 - abs(direction.yx)) * signValue;
    }
    return direction.xy * 0.5 + 0.5;
}

float2 LoadDirectionalVisibility(
    Texture2D visibilityTexture,
    uint sparseIndex,
    float3 probeToSurface)
{
    float2 octahedralUv = OctahedralEncode(
        normalize(probeToSurface));
    float2 tilePosition = clamp(
        octahedralUv * float(kVisibilityTileResolution) - 0.5,
        float2(0.0),
        float2(float(kVisibilityTileResolution - 1u)));
    uint2 tileMinimum = (uint2)floor(tilePosition);
    uint2 tileMaximum = min(
        tileMinimum + uint2(1u, 1u),
        uint2(
            kVisibilityTileResolution - 1u,
            kVisibilityTileResolution - 1u));
    float2 alpha = frac(tilePosition);
    uint visibilityBase =
        sparseIndex * kVisibilityTexelsPerProbe;
    uint index00 = visibilityBase +
        tileMinimum.y * kVisibilityTileResolution + tileMinimum.x;
    uint index10 = visibilityBase +
        tileMinimum.y * kVisibilityTileResolution + tileMaximum.x;
    uint index01 = visibilityBase +
        tileMaximum.y * kVisibilityTileResolution + tileMinimum.x;
    uint index11 = visibilityBase +
        tileMaximum.y * kVisibilityTileResolution + tileMaximum.x;
    float2 row0 = lerp(
        visibilityTexture.Load(int3(AtlasCoordinate(index00), 0)).rg,
        visibilityTexture.Load(int3(AtlasCoordinate(index10), 0)).rg,
        alpha.x);
    float2 row1 = lerp(
        visibilityTexture.Load(int3(AtlasCoordinate(index01), 0)).rg,
        visibilityTexture.Load(int3(AtlasCoordinate(index11), 0)).rg,
        alpha.x);
    return lerp(row0, row1, alpha.y);
}

float3 LoadSpecularLevel(
    Texture2D specularTexture,
    uint sparseIndex,
    float3 reflectionDirection,
    uint roughnessLevel)
{
    float2 octahedralUv = OctahedralEncode(
        normalize(reflectionDirection));
    float2 tilePosition = clamp(
        octahedralUv * float(kSpecularTileResolution) - 0.5,
        float2(0.0),
        float2(float(kSpecularTileResolution - 1u)));
    uint2 tileMinimum = (uint2)floor(tilePosition);
    uint2 tileMaximum = min(
        tileMinimum + uint2(1u, 1u),
        uint2(
            kSpecularTileResolution - 1u,
            kSpecularTileResolution - 1u));
    float2 alpha = frac(tilePosition);
    uint levelBase =
        sparseIndex * kSpecularTexelsPerProbe +
        roughnessLevel * kSpecularTexelsPerLevel;
    uint index00 = levelBase +
        tileMinimum.y * kSpecularTileResolution + tileMinimum.x;
    uint index10 = levelBase +
        tileMinimum.y * kSpecularTileResolution + tileMaximum.x;
    uint index01 = levelBase +
        tileMaximum.y * kSpecularTileResolution + tileMinimum.x;
    uint index11 = levelBase +
        tileMaximum.y * kSpecularTileResolution + tileMaximum.x;
    float3 row0 = lerp(
        specularTexture.Load(int3(AtlasCoordinate(index00), 0)).rgb,
        specularTexture.Load(int3(AtlasCoordinate(index10), 0)).rgb,
        alpha.x);
    float3 row1 = lerp(
        specularTexture.Load(int3(AtlasCoordinate(index01), 0)).rgb,
        specularTexture.Load(int3(AtlasCoordinate(index11), 0)).rgb,
        alpha.x);
    return lerp(row0, row1, alpha.y);
}

float3 LoadSpecularProbe(
    Texture2D specularTexture,
    uint sparseIndex,
    float3 reflectionDirection,
    float roughness)
{
    float level = saturate(roughness) *
        float(kSpecularRoughnessLevels - 1u);
    uint lowerLevel = (uint)floor(level);
    uint upperLevel = min(
        lowerLevel + 1u,
        kSpecularRoughnessLevels - 1u);
    return lerp(
        LoadSpecularLevel(
            specularTexture,
            sparseIndex,
            reflectionDirection,
            lowerLevel),
        LoadSpecularLevel(
            specularTexture,
            sparseIndex,
            reflectionDirection,
            upperLevel),
        frac(level));
}

bool SamplePersistentLevel(
    Texture2D irradTex,
    Texture2D visTex,
    float3 worldPosition,
    float3 worldNormal,
    uint level,
    out float3 irradianceValue,
    out float sampleConfidence)
{
    irradianceValue = float3(0.0);
    sampleConfidence = 0.0;
    uint levelState = 4u + level * 8u;
    float3 cellSize = float3(
        asfloat(push.DDGIVolumeState[levelState + 3u]),
        asfloat(push.DDGIVolumeState[levelState + 4u]),
        asfloat(push.DDGIVolumeState[levelState + 5u]));
    float3 gridSpace = worldPosition / cellSize;
    int3 baseCell = (int3)floor(gridSpace);
    float3 alpha = saturate(gridSpace - (float3)baseCell);
    float3 sumIrradiance = float3(0.0);
    float sumWeight = 0.0;
    float readyCornerWeight = 0.0;
    [unroll]
    for (int oz = 0; oz < 2; ++oz)
    [unroll]
    for (int oy = 0; oy < 2; ++oy)
    [unroll]
    for (int ox = 0; ox < 2; ++ox)
    {
        int3 cell = baseCell + int3(ox, oy, oz);
        int sparseIdx = LookupPersistentProbe(cell, level);
        if (!ProbeIsReady(sparseIdx))
            continue;

        uint colBase = (uint)sparseIdx * 4u;
        uint2 coordinate0 = AtlasCoordinate(colBase + 0u);
        uint2 coordinate1 = AtlasCoordinate(colBase + 1u);
        uint2 coordinate2 = AtlasCoordinate(colBase + 2u);
        uint2 coordinate3 = AtlasCoordinate(colBase + 3u);
        float3 probeIrradiance = EvaluateTwoBandSH(
            irradTex.Load(int3(coordinate0, 0)),
            irradTex.Load(int3(coordinate1, 0)),
            irradTex.Load(int3(coordinate2, 0)),
            irradTex.Load(int3(coordinate3, 0)),
            worldNormal);
        if (any(isnan(probeIrradiance)) ||
            any(isinf(probeIrradiance)))
            continue;

        float3 probePos = LookupProbeWorldPos(sparseIdx);
        float distanceToProbe = length(probePos - worldPosition);
        float2 moments = LoadDirectionalVisibility(
            visTex,
            (uint)sparseIdx,
            worldPosition - probePos);
        float visWeight = VisibilityWeight(
            moments.x,
            moments.y,
            distanceToProbe);
        float trilinearWeight =
            lerp(1.0 - alpha.x, alpha.x, (float)ox) *
            lerp(1.0 - alpha.y, alpha.y, (float)oy) *
            lerp(1.0 - alpha.z, alpha.z, (float)oz);
        float normalizedDistance =
            distanceToProbe / max(length(cellSize), 0.001);
        float distanceWeight = 1.0 /
            (1.0 + normalizedDistance * normalizedDistance);
        float weight = trilinearWeight * distanceWeight * visWeight;
        sumIrradiance += probeIrradiance * weight;
        sumWeight += weight;
        readyCornerWeight += trilinearWeight;
    }

    if (sumWeight <= 0.0001)
        return false;
    irradianceValue = sumIrradiance / sumWeight;
    sampleConfidence = saturate(readyCornerWeight);
    return true;
}

float3 SampleIndirectDiffuse(
    float3 worldPosition,
    float3 worldNormal,
    out float sampleConfidence)
{
    sampleConfidence = 0.0;
    if (!SparseLayoutReady())
        return float3(0.0);
    uint irradSlot = FetchIrradianceSlot();
    uint visSlot = FetchVisibilitySlot();
    if (!SlotIsValid(irradSlot) || !SlotIsValid(visSlot))
        return float3(0.0);
    if (push.DDGIVolumeState[0] != 0x44444749u)
        return float3(0.0);

    Texture2D irradTex = bindless.textures[irradSlot];
    Texture2D visTex = bindless.textures[visSlot];
    uint levelCount = min(push.DDGIVolumeState[1], 3u);
    bool hasSample = false;
    float3 combinedSample = float3(0.0);
    float combinedConfidence = 0.0;
    for (uint level = 0u; level < levelCount; ++level)
    {
        float3 levelSample;
        float levelConfidence;
        if (!SamplePersistentLevel(
                irradTex,
                visTex,
                worldPosition,
                worldNormal,
                level,
                levelSample,
                levelConfidence))
            continue;
        if (!hasSample)
        {
            combinedSample = levelSample;
            combinedConfidence = levelConfidence;
            hasSample = true;
        }
        else
        {
            combinedSample = lerp(
                levelSample,
                combinedSample,
                combinedConfidence);
            combinedConfidence +=
                (1.0 - combinedConfidence) * levelConfidence;
        }
        if (combinedConfidence >= 0.999)
        {
            sampleConfidence = combinedConfidence;
            return combinedSample;
        }
    }
    sampleConfidence = combinedConfidence;
    return hasSample ? combinedSample : float3(0.0);
}

bool SampleSpecularLevel(
    Texture2D specularTexture,
    Texture2D visibilityTexture,
    float3 worldPosition,
    float3 reflectionDirection,
    float roughness,
    uint level,
    out float3 radiance,
    out float sampleConfidence)
{
    radiance = float3(0.0);
    sampleConfidence = 0.0;
    uint levelState = 4u + level * 8u;
    float3 cellSize = float3(
        asfloat(push.DDGIVolumeState[levelState + 3u]),
        asfloat(push.DDGIVolumeState[levelState + 4u]),
        asfloat(push.DDGIVolumeState[levelState + 5u]));
    float3 gridSpace = worldPosition / cellSize;
    int3 baseCell = (int3)floor(gridSpace);
    float3 alpha = saturate(gridSpace - (float3)baseCell);
    float3 weightedRadiance = float3(0.0);
    float weightSum = 0.0;
    float readyCornerWeight = 0.0;
    [unroll]
    for (int oz = 0; oz < 2; ++oz)
    [unroll]
    for (int oy = 0; oy < 2; ++oy)
    [unroll]
    for (int ox = 0; ox < 2; ++ox)
    {
        int3 cell = baseCell + int3(ox, oy, oz);
        int sparseIdx = LookupPersistentProbe(cell, level);
        if (!ProbeSpecularIsReady(sparseIdx))
            continue;
        float3 probePos = LookupProbeWorldPos(sparseIdx);
        float distanceToProbe = length(probePos - worldPosition);
        float2 moments = LoadDirectionalVisibility(
            visibilityTexture,
            (uint)sparseIdx,
            worldPosition - probePos);
        float visibilityWeight = VisibilityWeight(
            moments.x,
            moments.y,
            distanceToProbe);
        float trilinearWeight =
            lerp(1.0 - alpha.x, alpha.x, (float)ox) *
            lerp(1.0 - alpha.y, alpha.y, (float)oy) *
            lerp(1.0 - alpha.z, alpha.z, (float)oz);
        float normalizedDistance =
            distanceToProbe / max(length(cellSize), 0.001);
        float distanceWeight = 1.0 /
            (1.0 + normalizedDistance * normalizedDistance);
        float weight = trilinearWeight * distanceWeight *
            visibilityWeight;
        weightedRadiance += LoadSpecularProbe(
            specularTexture,
            (uint)sparseIdx,
            reflectionDirection,
            roughness) * weight;
        weightSum += weight;
        readyCornerWeight += trilinearWeight;
    }
    if (weightSum <= 0.0001)
        return false;
    radiance = weightedRadiance / weightSum;
    sampleConfidence = saturate(readyCornerWeight);
    return true;
}

float3 SampleIndirectSpecular(
    float3 worldPosition,
    float3 reflectionDirection,
    float roughness,
    out float sampleConfidence)
{
    sampleConfidence = 0.0;
    if (!SparseLayoutReady() || push.DDGISpecularParams.w < 0.5)
        return float3(0.0);
    uint specularSlot = FetchSpecularSlot();
    uint visibilitySlot = FetchVisibilitySlot();
    if (!SlotIsValid(specularSlot) || !SlotIsValid(visibilitySlot))
        return float3(0.0);
    if (push.DDGIVolumeState[0] != 0x44444749u)
        return float3(0.0);
    Texture2D specularTexture = bindless.textures[specularSlot];
    Texture2D visibilityTexture = bindless.textures[visibilitySlot];
    uint levelCount = min(push.DDGIVolumeState[1], 3u);
    bool hasSample = false;
    float3 combinedSample = float3(0.0);
    float combinedConfidence = 0.0;
    for (uint level = 0u; level < levelCount; ++level)
    {
        float3 levelSample;
        float levelConfidence;
        if (!SampleSpecularLevel(
                specularTexture,
                visibilityTexture,
                worldPosition,
                reflectionDirection,
                roughness,
                level,
                levelSample,
                levelConfidence))
            continue;
        if (!hasSample)
        {
            combinedSample = levelSample;
            combinedConfidence = levelConfidence;
            hasSample = true;
        }
        else
        {
            combinedSample = lerp(
                levelSample,
                combinedSample,
                combinedConfidence);
            combinedConfidence +=
                (1.0 - combinedConfidence) * levelConfidence;
        }
        if (combinedConfidence >= 0.999)
            break;
    }
    sampleConfidence = combinedConfidence;
    return hasSample ? combinedSample : float3(0.0);
}

struct DDGIShadingResult
{
    float3 irradiance;
    float3 indirectDiffuse;
    float3 indirectSpecular;
    float sampleConfidence;
    float specularConfidence;
};

DDGIShadingResult EvaluateDDGIShading(
    float3 worldPosition,
    float3 worldNormal,
    float3 viewDirection,
    float3 albedo,
    float metallic,
    float roughness)
{
    DDGIShadingResult result;
    result.irradiance = SampleIndirectDiffuse(
        worldPosition,
        worldNormal,
        result.sampleConfidence);
    if (any(isnan(result.irradiance)) ||
        any(isinf(result.irradiance)))
    {
        result.irradiance = float3(0.0);
        result.sampleConfidence = 0.0;
    }
    result.irradiance = clamp(
        result.irradiance,
        float3(0.0),
        float3(8.0));
    result.indirectDiffuse = clamp(
        result.irradiance * albedo *
            (1.0 - metallic) * 0.318309886 *
            saturate(result.sampleConfidence),
        float3(0.0),
        float3(8.0));
    float3 reflectionDirection = reflect(
        -viewDirection,
        worldNormal);
    float3 reflectedRadiance = SampleIndirectSpecular(
        worldPosition,
        reflectionDirection,
        roughness,
        result.specularConfidence);
    float dotNV = saturate(dot(worldNormal, viewDirection));
    float3 f0 = lerp(float3(0.04), albedo, saturate(metallic));
    float3 fresnel = f0 + (1.0 - f0) *
        pow(1.0 - dotNV, 5.0);
    result.indirectSpecular = clamp(
        reflectedRadiance * fresnel *
            saturate(result.specularConfidence),
        float3(0.0),
        float3(8.0));
    return result;
}

float3 EvaluateDDGILightingOnly(DDGIShadingResult result)
{
    return result.irradiance * 0.318309886 *
        saturate(result.sampleConfidence) +
        result.indirectSpecular;
}

float3 ApplyDDGIAmbientPolicy(
    float3 fallbackAmbient,
    DDGIShadingResult result)
{
    return fallbackAmbient *
        (1.0 - saturate(result.sampleConfidence));
}

float3 ApplyDDGIConsumerDebug(
    float3 shadedColor,
    DDGIShadingResult result)
{
    uint consumerFlags = (uint)push.DDGIAtlasParams.z;
    if ((consumerFlags & 1u) != 0u)
        return result.irradiance * 0.318309886 *
            saturate(result.sampleConfidence);
    return shadedColor;
}
