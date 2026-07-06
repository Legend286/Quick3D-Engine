// minimal_test.slang — absolute minimal compute shader to validate pipeline
[[vk::binding(4, 0)]] RWTexture2D<float4> outputBuffer;

[shader("compute")]
[numthreads(64, 1, 1)]
void computeMain(uint3 dispatchThreadID : SV_DispatchThreadID) {
    outputBuffer[dispatchThreadID.xy] = float4(1.0, 0.0, 1.0, 1.0);
}
