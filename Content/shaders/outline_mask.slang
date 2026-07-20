#include "scene_data.slang"

[push_constant]
ConstantBuffer<ScenePushData> push;

struct VertexOutput
{
    float4 position : SV_Position;
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
    
    // Apply depth bias to pull the mask slightly towards the camera
    // This prevents Z-fighting when depth-testing against the jittered PathTracer depth buffer
    output.position.z -= 0.05 * output.position.w;
    
    return output;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput vin) : SV_Target
{
    return float4(1.0, 1.0, 1.0, 1.0); // Pure white mask
}
