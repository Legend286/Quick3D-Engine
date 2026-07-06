// SPDX-License-Identifier: MIT

struct CameraData { 
    float4x4 viewProj; 
};

struct TriangleVOut {
    float4 position : SV_Position;
    float3 color    : TEXCOORD0;
};

// These map to [[buffer(0)]] and [[buffer(1)]] in the MSL target usually,
// BUT our push constants are mapped to buffer(0). 
// Wait, in Metal we bound push constants to slot 0:
// `[ri->render setVertexBytes:data length:size atIndex:0];`
// So we should register our buffers to t1 and t2 to avoid collision with push constants!

[[vk::binding(1, 0)]] StructuredBuffer<float3> positions;
[[vk::binding(2, 0)]] StructuredBuffer<float3> colors;

[push_constant]
ConstantBuffer<CameraData> cam;

[shader("vertex")]
TriangleVOut vertexMain(uint vid : SV_VertexID)
{
    TriangleVOut o;
    float4 worldPos = float4(positions[vid], 1.0);
    o.position = mul(cam.viewProj, worldPos);
    
    // For fallback mesh, colors is valid.
    // If we only bind position buffer for loaded mesh, this might read out of bounds or garbage.
    // We should probably just use a solid color if we don't have colors.
    o.color = colors[vid]; 
    return o;
}

[shader("fragment")]
float4 fragmentMain(TriangleVOut in) : SV_Target
{
    return float4(in.color, 1.0);
}
