struct PreviewPushData {
    float4 uvScaleBias;
};

struct VertexOutput {
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[[vk::push_constant]]
ConstantBuffer<PreviewPushData> push;

[[vk::binding(1, 0)]] Texture2D<float> shadowAtlas;
[[vk::binding(2, 0)]] SamplerState previewSampler;

[shader("vertex")]
VertexOutput vertexMain(uint vertexId : SV_VertexID)
{
    VertexOutput output;
    output.uv = float2((vertexId << 1) & 2, vertexId & 2);
    output.position = float4(
        output.uv * 2.0 - 1.0,
        0.0,
        1.0);
    output.uv.y = 1.0 - output.uv.y;
    return output;
}

[shader("fragment")]
float4 fragmentMain(VertexOutput input) : SV_Target
{
    float2 uv =
        input.uv * push.uvScaleBias.xy +
        push.uvScaleBias.zw;
    float depth = shadowAtlas.Sample(previewSampler, uv);
    float visibility = pow(saturate(1.0 - depth), 0.25);
    float3 color = lerp(
        float3(0.025, 0.035, 0.055),
        float3(0.85, 0.92, 1.0),
        visibility);
    return float4(color, 1.0);
}
