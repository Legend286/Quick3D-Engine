struct CameraData {
    float4x4 viewProj;
    float4x4 invViewProj;
    float4 cameraPosition;
};

[push_constant]
ConstantBuffer<CameraData> push;

StructuredBuffer<float3> vtxBuffer : register(t1);

struct VertexOutput {
    float4 position : SV_Position;
    float3 worldPos : POSITION;
};

[shader("vertex")]
VertexOutput vertexMain(uint vid : SV_VertexID) {
    VertexOutput output;
    
    float3 pos = vtxBuffer[vid];
    output.worldPos = pos;
    output.position = mul(push.viewProj, float4(pos, 1.0));
    
    return output;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target {
    // Fade out lines based on distance to camera
    float dist = length(input.worldPos - push.cameraPosition.xyz);
    float fade = max(0.0, 1.0 - (dist / 100.0));
    
    float3 color = float3(0.5, 0.5, 0.5); // Default grey line
    
    // Highlight axes
    if (abs(input.worldPos.x) < 0.1) {
        color = float3(0.2, 0.2, 1.0); // Z axis blue
    }
    if (abs(input.worldPos.z) < 0.1) {
        color = float3(1.0, 0.2, 0.2); // X axis red
    }
    
    return float4(color, fade * 0.8);
}
