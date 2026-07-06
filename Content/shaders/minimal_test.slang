// minimal_test.slang — absolute minimal compute shader to validate pipeline
RWTexture2D<float4> outputBuffer : register(u4);

[shader("compute")]
[numthreads(64, 1, 1)]
void computeMain(uint3 dispatchThreadID : SV_DispatchThreadID) {
    outputBuffer[dispatchThreadID.xy] = float4(1.0, 0.0, 1.0, 1.0);
}
