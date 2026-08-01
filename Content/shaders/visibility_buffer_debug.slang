// SPDX-License-Identifier: MIT
#include "visibility_debug_common.slang"

struct VisibilityDebugPush
{
    uint width;
    uint height;
    uint mode;
    uint padding;
};

[[vk::push_constant]]
ConstantBuffer<VisibilityDebugPush> push;

[[vk::binding(0, 0)]] Texture2D<uint2> visibilityIdentifiers;
[[vk::binding(1, 0)]] Texture2D<float2> visibilityBarycentrics;
[[vk::binding(2, 0)]] Texture2D<float> sceneDepth;
[[vk::binding(3, 0)]] Texture2D<float4> reconstructionOutput;
[[vk::binding(4, 0)]] Texture2D<float4> referenceOutput;

struct VisibilityDebugVertexOutput
{
    float4 position : SV_Position;
};

[shader("vertex")]
VisibilityDebugVertexOutput vertexMain(uint vertexIndex : SV_VertexID)
{
    VisibilityDebugVertexOutput output;
    float2 coordinate = float2(
        (vertexIndex << 1u) & 2u,
        vertexIndex & 2u);
    output.position = float4(
        coordinate * 2.0 - 1.0,
        0.0,
        1.0);
    return output;
}

float3 VisualizeReconstructedValue(float3 value, uint mode)
{
    if (mode == 14u)
        return frac(abs(value) * 0.1);
    if (mode == 15u || mode == 19u)
        return value * 0.5 + 0.5;
    if (mode == 16u)
        return float3(frac(value.xy), 0.0);
    return value;
}

float ReconstructionErrorAmplification(uint mode)
{
    if (mode == 14u)
        return 128.0;
    if (mode == 16u)
        return 128.0;
    if (mode == 17u || mode == 18u)
        return 32.0;
    if (mode == 20u)
        return 8.0;
    return 32.0;
}

[shader("fragment")]
float4 fragmentMain(VisibilityDebugVertexOutput input) : SV_Target
{
    uint2 dimensions = uint2(
        max(push.width, 1u),
        max(push.height, 1u));
    uint2 pixel = min(
        uint2(input.position.xy),
        dimensions - 1u);
    float depth = sceneDepth.Load(int3(pixel, 0));
    if (depth >= 0.999999 && push.mode != 20u)
        return float4(0.012, 0.015, 0.022, 1.0);

    if (push.mode >= 14u && push.mode <= 20u)
    {
        float3 reference = referenceOutput.Load(int3(pixel, 0)).rgb;
        float3 reconstructed =
            reconstructionOutput.Load(int3(pixel, 0)).rgb;
        float3 referenceDisplay =
            VisualizeReconstructedValue(reference, push.mode);
        float3 reconstructedDisplay =
            VisualizeReconstructedValue(reconstructed, push.mode);
        uint dividerDistance = pixel.x > dimensions.x / 2u
            ? pixel.x - dimensions.x / 2u
            : dimensions.x / 2u - pixel.x;
        if (dividerDistance <= 1u)
            return float4(1.0, 0.9, 0.15, 1.0);
        if (pixel.x < dimensions.x / 2u)
            return float4(referenceDisplay, 1.0);
        float error = saturate(
            length(reference - reconstructed) *
            ReconstructionErrorAmplification(push.mode));
        float3 compared = lerp(
            reconstructedDisplay,
            float3(1.0, 0.0, 0.0),
            error);
        return float4(compared, 1.0);
    }

    uint2 identifiers = visibilityIdentifiers.Load(int3(pixel, 0));
    float2 barycentricXY =
        visibilityBarycentrics.Load(int3(pixel, 0));
    float3 barycentrics = max(
        float3(
            barycentricXY,
            1.0 - barycentricXY.x - barycentricXY.y),
        0.0);
    barycentrics /= max(
        barycentrics.x + barycentrics.y + barycentrics.z,
        1.0e-6);
    return float4(
        VisibilityDebugColor(
            identifiers,
            barycentrics,
            pixel.x,
            dimensions.x),
        1.0);
}
