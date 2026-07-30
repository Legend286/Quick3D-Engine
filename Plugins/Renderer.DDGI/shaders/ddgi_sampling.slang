float3 SampleIndirectDiffuse(float3 worldPosition, float3 worldNormal)
{
    float upFacing = saturate(worldNormal.y);
    return float3(0.05 + 0.4 * upFacing, 0.08 + 0.05 * upFacing, 0.12);
}
