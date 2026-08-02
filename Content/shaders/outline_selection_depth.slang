#include "scene_data.slang"

[push_constant]
ConstantBuffer<ScenePushData> push;

struct VertexOutput
{
    float4 position : SV_Position;
};

[shader("vertex")]
VertexOutput vertexMain(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
{
    PartData part = push.parts[instanceId];
    InstanceData instance = push.instances[part.instanceIdx];
    uint vertexIndex = LoadPartVertexIndex(part, vertexId);
    Vertex vertex = part.vertices[vertexIndex];
    float3 localPosition =
        float3(vertex.px, vertex.py, vertex.pz) +
        part.localOffset.xyz;
    float4 worldPosition = mul(
        instance.modelMatrix,
        float4(localPosition, 1.0));
    VertexOutput output;
    output.position = mul(push.camera->viewProj, worldPosition);
    return output;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target
{
    return 0.0;
}
