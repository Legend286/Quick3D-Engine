// blit_test.slang — absolute minimal blit to validate entire pipeline
struct VSOut {
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

[shader("vertex")]
VSOut vertexMain(uint vid : SV_VertexID) {
    VSOut output;
    output.uv = float2((vid << 1) & 2, vid & 2);
    output.position = float4(output.uv * 2.0 - 1.0, 0.0, 1.0);
    output.uv.y = 1.0 - output.uv.y;
    return output;
}

[shader("fragment")]
float4 fragmentMain(VSOut input) : SV_Target {
    // Diagnostic: bright magenta solid fill — no texture sampling at all
    return float4(1.0, 0.0, 1.0, 1.0);
}
