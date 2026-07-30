#include "scene_data.slang"

[push_constant]
ConstantBuffer<ScenePushData> push;

struct ShadowVertexOutput {
    float4 position : SV_Position;
};

[shader("vertex")]
ShadowVertexOutput vertexMain(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    PartData part = push.parts[iid];
    InstanceData inst = push.instances[part.instanceIdx];
    uint index = part.indices[vid];
    Vertex vertex = part.vertices[index];
    float3 localPosition =
        float3(vertex.px, vertex.py, vertex.pz) +
        part.localOffset.xyz;
    float4 worldPosition = mul(
        inst.modelMatrix,
        float4(localPosition, 1.0));

    ShadowVertexOutput output;
    output.position = mul(push.directionalShadowViewProj, worldPosition);
    return output;
}

[shader("fragment")]
void fragmentMain()
{
}
