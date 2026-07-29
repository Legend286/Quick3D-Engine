struct ClearVertexOutput {
    float4 position : SV_Position;
};

[shader("vertex")]
ClearVertexOutput vertexMain(uint vertexId : SV_VertexID)
{
    float2 positions[3] = {
        float2(-1.0, -1.0),
        float2(3.0, -1.0),
        float2(-1.0, 3.0)
    };
    ClearVertexOutput output;
    output.position = float4(positions[vertexId], 1.0, 1.0);
    return output;
}

[shader("fragment")]
void fragmentMain()
{
}
