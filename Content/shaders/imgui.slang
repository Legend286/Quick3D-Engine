// imgui.slang

struct VertexOutput
{
    float4 pos   : SV_Position;
    float4 color : COLOR;
    float2 uv    : TEXCOORD;
};

// Push constants for projection matrix
struct PushConstants
{
    float2 scale;
    float2 translate;
};

[push_constant]
ConstantBuffer<PushConstants> u_pushConstants;

[[vk::binding(1, 0)]] ByteAddressBuffer vtxBuffer;

[shader("vertex")]
VertexOutput vertexMain(uint vid : SV_VertexID)
{
    uint offset = vid * 20;
    float2 v_pos = asfloat(vtxBuffer.Load2(offset));
    float2 v_uv = asfloat(vtxBuffer.Load2(offset + 8));
    uint v_col = vtxBuffer.Load(offset + 16);
    
    VertexOutput output;
    output.pos = float4(v_pos * u_pushConstants.scale + u_pushConstants.translate, 0.0, 1.0);
    
    // Decode ABGR to float4 (ImGui col is typically ABGR in memory for little endian RGBA)
    output.color = float4(
        (v_col & 0xFF) / 255.0,
        ((v_col >> 8) & 0xFF) / 255.0,
        ((v_col >> 16) & 0xFF) / 255.0,
        ((v_col >> 24) & 0xFF) / 255.0
    );
    output.uv = v_uv;
    return output;
}

// Texture for font atlas
[[vk::binding(0, 0)]] Texture2D t_fontTexture;

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target
{
    uint width, height;
    t_fontTexture.GetDimensions(width, height);
    int2 coords = int2(input.uv.x * width, input.uv.y * height);
    return input.color * t_fontTexture.Load(int3(coords, 0));
}
