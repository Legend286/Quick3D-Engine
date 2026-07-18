struct PushConstants
{
    uint MaskTextureSlot;
    uint Width;
    uint Height;
    uint Pad;
};

[[vk::push_constant]]
ConstantBuffer<PushConstants> push;

struct BindlessHeap {
    Texture2D textures[4096];
};

[[vk::binding(0, 0)]] SamplerState smp;
[[vk::binding(1, 0)]] ParameterBlock<BindlessHeap> bindless;

struct VertexOut
{
    float4 Position : SV_Position;
    float2 TexCoord : TEXCOORD;
};

[shader("vertex")]
VertexOut vertexMain(uint vertexID : SV_VertexID)
{
    VertexOut vout;
    // Generate a fullscreen triangle
    vout.TexCoord = float2((vertexID << 1) & 2, vertexID & 2);
    vout.Position = float4(vout.TexCoord * 2.0 - 1.0, 0.0, 1.0);
    return vout;
}

[shader("fragment")]
float4 fragmentMain(VertexOut vin) : SV_Target
{
    Texture2D maskTex = bindless.textures[push.MaskTextureSlot];
    
    float2 texelSize = float2(1.0 / float(push.Width), 1.0 / float(push.Height));
    float2 uv = vin.TexCoord;
    uv.y = 1.0 - uv.y; // NDC Y flip

    // Read center pixel
    float center = maskTex.Sample(smp, uv).r;

    // Read neighbors (cross) with clamping to prevent wrap-around
    float n = maskTex.Sample(smp, clamp(uv + float2(0, -texelSize.y), 0.0, 1.0)).r;
    float s = maskTex.Sample(smp, clamp(uv + float2(0, texelSize.y), 0.0, 1.0)).r;
    float e = maskTex.Sample(smp, clamp(uv + float2(texelSize.x, 0), 0.0, 1.0)).r;
    float w = maskTex.Sample(smp, clamp(uv + float2(-texelSize.x, 0), 0.0, 1.0)).r;

    float sum = n + s + e + w;

    // Edge detection: if center is NOT masked, but neighbors ARE masked
    // (i.e. we are outside the object, but right next to it)
    // We use > 0.5 for sum because the background clear color is (0.05, 0.06, 0.09) in rhi_metal
    if (center < 0.1 && sum > 0.5)
    {
        return float4(1.0, 0.5, 0.0, 1.0); // Orange outline
    }
    
    // Transparent everywhere else
    return float4(0.0, 0.0, 0.0, 0.0);
}
