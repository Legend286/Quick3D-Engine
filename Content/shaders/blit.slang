// SPDX-License-Identifier: MIT
// Fullscreen blit: reads from a storage texture and writes to the render target.

struct VSOut {
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[binding(1, 0)] Texture2D<float4> sourceTexture;
[binding(2, 0)] SamplerState blitSampler;

[shader("vertex")]
VSOut vertexMain(uint vid : SV_VertexID) {
    VSOut output;
    // Fullscreen triangle: vertices 0,1,2 cover the screen
    output.uv = float2((vid << 1) & 2, vid & 2);
    output.position = float4(output.uv * 2.0 - 1.0, 0.0, 1.0);
    output.uv.y = 1.0 - output.uv.y;
    return output;
}

[shader("fragment")]
float4 fragmentMain(VSOut input) : SV_Target {
    return sourceTexture.Sample(blitSampler, input.uv);
}
