// SPDX-License-Identifier: MIT
#include "scene_data.slang"

[push_constant]
ConstantBuffer<ScenePushData> push;

struct VisibilityVertexOutput
{
    float4 position : SV_Position;
    nointerpolation uint rasterInstanceIndex : BLENDINDICES0;
};

struct VisibilityFragmentOutput
{
    uint2 identifiers : SV_Target0;
    float2 barycentrics : SV_Target1;
};

[shader("vertex")]
VisibilityVertexOutput vertexMain(
    uint vertexIndex : SV_VertexID,
    uint rasterInstanceIndex : SV_InstanceID)
{
    VisibilityVertexOutput output;
    PartData part = push.parts[rasterInstanceIndex];
    InstanceData instance = push.instances[part.instanceIdx];
    uint index = LoadPartVertexIndex(part, vertexIndex);
    Vertex vertex = part.vertices[index];
    float3 localPosition =
        float3(vertex.px, vertex.py, vertex.pz) +
        part.localOffset.xyz;
    float4 worldPosition = mul(
        instance.modelMatrix,
        float4(localPosition, 1.0));
    output.position = mul(push.camera->viewProj, worldPosition);
    output.rasterInstanceIndex = rasterInstanceIndex;
    return output;
}

[shader("fragment")]
VisibilityFragmentOutput fragmentMain(
    VisibilityVertexOutput input,
    uint primitiveIndex : SV_PrimitiveID,
    float3 barycentrics : SV_Barycentrics)
{
    VisibilityFragmentOutput output;
    output.identifiers = uint2(
        input.rasterInstanceIndex,
        primitiveIndex);
    output.barycentrics = saturate(barycentrics.xy);
    return output;
}
