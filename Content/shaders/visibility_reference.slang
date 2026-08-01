// SPDX-License-Identifier: MIT
#include "scene_data.slang"
#include "visibility_debug_common.slang"

[push_constant]
ConstantBuffer<ScenePushData> push;

struct BindlessHeap
{
    Texture2D textures[4096];
};
[[vk::binding(1, 0)]] ParameterBlock<BindlessHeap> bindless;
[[vk::binding(2, 0)]] SamplerState textureSampler;

struct VisibilityReferenceVertexOutput
{
    float4 position : SV_Position;
    float3 worldPosition : POSITION;
    float3 normal : NORMAL;
    float4 tangent : TANGENT;
    float2 uv : TEXCOORD0;
    nointerpolation uint materialIndex : BLENDINDICES0;
    nointerpolation uint instanceIndex : BLENDINDICES1;
    nointerpolation uint entityIdLow : BLENDINDICES2;
    nointerpolation uint entityIdHigh : BLENDINDICES3;
};

[shader("vertex")]
VisibilityReferenceVertexOutput vertexMain(
    uint vertexIndex : SV_VertexID,
    uint partIndex : SV_InstanceID)
{
    VisibilityReferenceVertexOutput output;
    PartData part = push.parts[partIndex];
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
    output.worldPosition = worldPosition.xyz;
    output.normal = normalize(mul(
        (float3x3)instance.modelMatrix,
        float3(vertex.nx, vertex.ny, vertex.nz)));
    output.tangent = float4(
        normalize(mul(
            (float3x3)instance.modelMatrix,
            float3(vertex.tx, vertex.ty, vertex.tz))),
        vertex.tw);
    output.uv = float2(vertex.u, vertex.v);
    output.materialIndex = part.materialIdx;
    output.instanceIndex = part.instanceIdx;
    output.entityIdLow = instance.entityIdLow;
    output.entityIdHigh = instance.entityIdHigh;
    return output;
}

void BuildReferenceTangentFrame(
    VisibilityReferenceVertexOutput input,
    out float3 normal,
    out float3 tangent)
{
    normal = normalize(input.normal);
    tangent = normalize(input.tangent.xyz);
    if (!(dot(tangent, tangent) > 0.001))
    {
        float3 up = abs(normal.y) < 0.999
            ? float3(0.0, 1.0, 0.0)
            : float3(1.0, 0.0, 0.0);
        tangent = normalize(cross(up, normal));
    }
    float3 bitangent = cross(normal, tangent) * input.tangent.w;
    if (!(dot(bitangent, bitangent) > 0.001))
        bitangent = cross(normal, tangent);
    bitangent = normalize(bitangent);
    tangent = normalize(cross(bitangent, normal));

    MaterialData material = push.materials[input.materialIndex];
    if (material.normalTexIndex != 0xffffffffu)
    {
        float3 tangentNormal =
            bindless.textures[material.normalTexIndex]
                .Sample(textureSampler, input.uv).rgb * 2.0 - 1.0;
        normal = normalize(mul(
            tangentNormal,
            float3x3(tangent, bitangent, normal)));
        bitangent = cross(normal, tangent) * input.tangent.w;
        if (!(dot(bitangent, bitangent) > 0.001))
            bitangent = cross(normal, tangent);
        bitangent = normalize(bitangent);
        tangent = normalize(cross(bitangent, normal));
    }
}

[shader("fragment")]
float4 fragmentMain(VisibilityReferenceVertexOutput input) : SV_Target
{
    uint debugView = push.debugFlags & 0xffu;
    float3 normal = 0.0;
    float3 tangent = 0.0;
    if (debugView == 15u || debugView == 19u)
        BuildReferenceTangentFrame(input, normal, tangent);
    float3 value = float3(1.0, 0.0, 1.0);
    if (debugView == 14u)
        value = input.worldPosition;
    else if (debugView == 15u)
        value = normal;
    else if (debugView == 16u)
        value = float3(input.uv, 0.0);
    else if (debugView == 17u)
        value = VisibilityIdentifierColor(
            uint2(input.materialIndex, 0x4d41544cu));
    else if (debugView == 18u)
        value = VisibilityIdentifierColor(
            uint2(
                input.instanceIndex,
                input.entityIdLow ^ input.entityIdHigh));
    else if (debugView == 19u)
        value = tangent;
    return float4(value, 1.0);
}
