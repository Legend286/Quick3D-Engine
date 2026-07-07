// SPDX-License-Identifier: MIT
// Fullscreen blit: reads from a storage texture and writes to the render target.

struct VSOut {
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[[vk::binding(1, 0)]] Texture2D<float4> sourceTexture;
[[vk::binding(2, 0)]] SamplerState blitSampler;

[shader("vertex")]
VSOut vertexMain(uint vid : SV_VertexID) {
    VSOut output;
    // Fullscreen triangle: vertices 0,1,2 cover the screen
    output.uv = float2((vid << 1) & 2, vid & 2);
    output.position = float4(output.uv * 2.0 - 1.0, 0.0, 1.0);
    output.uv.y = 1.0 - output.uv.y;
    return output;
}

struct FSOut {
    float4 color : SV_Target;
    float depth : SV_Depth;
};

[shader("fragment")]
FSOut fragmentMain(VSOut input) {
    float4 src = sourceTexture.Sample(blitSampler, input.uv);
    FSOut output;
    output.color = float4(src.rgb, 1.0);
    output.depth = src.a;
    return output;
}
