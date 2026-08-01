// SPDX-License-Identifier: MIT

uint VisibilityDebugHash(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    return value ^ (value >> 16u);
}

float3 VisibilityIdentifierColor(uint2 identifiers)
{
    uint partHash = VisibilityDebugHash(
        identifiers.x ^ 0x9e3779b9u);
    uint primitiveHash = VisibilityDebugHash(
        identifiers.y ^ 0x85ebca6bu);
    primitiveHash =
        (primitiveHash << 16u) |
        (primitiveHash >> 16u);
    uint colorHash = VisibilityDebugHash(
        partHash ^ primitiveHash ^ 0xc2b2ae35u);
    float hue =
        float(colorHash & 0x00ffffffu) /
        16777216.0;
    float saturation =
        0.72 +
        0.23 * float((colorHash >> 24u) & 0x0fu) / 15.0;
    float value =
        0.78 +
        0.22 * float((colorHash >> 28u) & 0x0fu) / 15.0;
    float3 hueRgb = saturate(
        abs(
            frac(
                hue +
                float3(0.0, 0.6666667, 0.3333333)) *
                6.0 -
                3.0) -
        1.0);
    return value * lerp(float3(1.0), hueRgb, saturation);
}

float3 VisibilityDebugColor(
    uint2 identifiers,
    float3 barycentrics,
    uint pixelX,
    uint width)
{
    if (pixelX < width / 2u)
        return VisibilityIdentifierColor(identifiers);
    return barycentrics;
}
