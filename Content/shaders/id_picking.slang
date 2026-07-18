// id_picking.slang

#include "scene_data.slang"

[push_constant]
ConstantBuffer<ScenePushData> push;

struct VertexOutput {
    float4 position : SV_Position;
    uint entityIdLow : BLENDINDICES0;
};

[shader("vertex")]
VertexOutput vertexMain(uint vid : SV_VertexID, uint iid : SV_InstanceID) 
{
    VertexOutput output;
    
    PartData part = push.parts[iid];
    InstanceData inst = push.instances[part.instanceIdx];
    
    uint index = part.indices[vid];
    Vertex v = part.vertices[index];
    
    float3 vPos = float3(v.px, v.py, v.pz);
    
    float4 worldPos = mul(inst.modelMatrix, float4(vPos, 1.0));
    output.position = mul(push.camera->viewProj, worldPos);
    output.entityIdLow = inst.entityIdLow;
    
    return output;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target 
{
    uint id = input.entityIdLow;
    float r = float((id >> 0)  & 0xFF) / 255.0;
    float g = float((id >> 8)  & 0xFF) / 255.0;
    float b = float((id >> 16) & 0xFF) / 255.0;
    float a = float((id >> 24) & 0xFF) / 255.0;
    return float4(r, g, b, a);
}
