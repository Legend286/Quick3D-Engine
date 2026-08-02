#include "scene_data.slang"

struct PushConstants
{
    PartData* parts;
    InstanceData* instances;
    uint partCount;
    uint selectedEntityLow;
    uint selectedEntityHigh;
    uint width;
    uint height;
    float depthEpsilon;
    float outlineRadius;
    uint pad0;
};

[[vk::push_constant]]
ConstantBuffer<PushConstants> push;

[[vk::binding(0, 0)]]
Texture2D<uint2> visibilityIdentifiers : register(t0);
[[vk::binding(1, 0)]]
Texture2D<float> sceneDepth : register(t1);
[[vk::binding(2, 0)]]
Texture2D<float> selectionDepth : register(t2);

struct VertexOutput
{
    float4 position : SV_Position;
};

[shader("vertex")]
VertexOutput vertexMain(uint vertexId : SV_VertexID)
{
    float2 uv = float2((vertexId << 1) & 2, vertexId & 2);
    VertexOutput output;
    output.position = float4(uv * 2.0 - 1.0, 0.0, 1.0);
    return output;
}

int2 ClampPixel(int2 pixel)
{
    return clamp(
        pixel,
        int2(0, 0),
        int2(int(push.width) - 1, int(push.height) - 1));
}

float LoadSelectionDepth(int2 pixel)
{
    return selectionDepth.Load(int3(ClampPixel(pixel), 0));
}

bool HasSelection(int2 pixel)
{
    return LoadSelectionDepth(pixel) < 0.999999;
}

bool VisibilityMatchesSelection(int2 pixel)
{
    int2 clampedPixel = ClampPixel(pixel);
    float depth = sceneDepth.Load(int3(clampedPixel, 0));
    if (depth >= 0.999999)
        return false;
    uint2 identifiers =
        visibilityIdentifiers.Load(int3(clampedPixel, 0));
    if (identifiers.x >= push.partCount)
        return false;
    PartData part = push.parts[identifiers.x];
    InstanceData instance = push.instances[part.instanceIdx];
    return instance.entityIdLow == push.selectedEntityLow &&
        instance.entityIdHigh == push.selectedEntityHigh;
}

bool SelectionIsOccluded(int2 pixel)
{
    int2 clampedPixel = ClampPixel(pixel);
    float selected = selectionDepth.Load(int3(clampedPixel, 0));
    if (selected >= 0.999999 ||
        VisibilityMatchesSelection(clampedPixel))
    {
        return false;
    }
    float scene = sceneDepth.Load(int3(clampedPixel, 0));
    return scene < 0.999999 &&
        selected > scene + push.depthEpsilon;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target
{
    int2 pixel = int2(input.position.xy);
    bool centerSelected = HasSelection(pixel);
    float nearestOpposite = 100.0;
    int2 nearestSelectedPixel = pixel;
    for (int y = -3; y <= 3; ++y)
    {
        for (int x = -3; x <= 3; ++x)
        {
            int2 offset = int2(x, y);
            float distancePixels = length(float2(offset));
            if (distancePixels > push.outlineRadius + 0.35)
                continue;
            int2 samplePixel = ClampPixel(pixel + offset);
            bool sampleSelected = HasSelection(samplePixel);
            if (sampleSelected == centerSelected ||
                distancePixels >= nearestOpposite)
            {
                continue;
            }
            nearestOpposite = distancePixels;
            if (sampleSelected)
                nearestSelectedPixel = samplePixel;
        }
    }

    bool hasEdge = nearestOpposite < 99.0;
    int2 classificationPixel = centerSelected
        ? pixel
        : nearestSelectedPixel;
    bool visible = VisibilityMatchesSelection(classificationPixel);
    bool occluded = SelectionIsOccluded(classificationPixel);
    if (!visible && !occluded && HasSelection(classificationPixel))
        visible = true;

    float softEdge = hasEdge
        ? 1.0 - smoothstep(
            push.outlineRadius - 0.9,
            push.outlineRadius + 0.15,
            nearestOpposite)
        : 0.0;
    float crispEdge = hasEdge
        ? 1.0 - smoothstep(0.85, 1.75, nearestOpposite)
        : 0.0;
    float edgeAlpha = max(crispEdge * 0.96, softEdge * 0.52);
    float3 visibleColor = float3(1.0, 0.36, 0.035);
    float3 occludedColor = float3(0.58, 0.26, 1.0);
    float3 edgeColor = occluded ? occludedColor : visibleColor;

    float fillAlpha = 0.0;
    if (centerSelected)
    {
        if (occluded)
        {
            float selected = LoadSelectionDepth(pixel);
            float scene = sceneDepth.Load(int3(ClampPixel(pixel), 0));
            float separation = saturate(
                (selected - scene - push.depthEpsilon) * 320.0);
            fillAlpha = lerp(0.075, 0.145, separation);
        }
        else if (visible)
        {
            fillAlpha = 0.028;
        }
    }

    float alpha = max(edgeAlpha, fillAlpha);
    if (alpha <= 0.0001)
        return 0.0;
    float3 fillColor = occluded
        ? occludedColor
        : visibleColor;
    float edgeMix = edgeAlpha / max(alpha, 0.0001);
    float3 color = lerp(fillColor * 0.72, edgeColor, edgeMix);
    return float4(color, alpha);
}
