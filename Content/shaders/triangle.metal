// SPDX-License-Identifier: MIT
// Hello-triangle Metal Shading Language shader used by Phase 2 hello-triangle pass.
// Compiled at runtime via MTLDevice.newLibraryWithSource.

#include <metal_stdlib>
using namespace metal;

struct TriangleVertex {
    float3 position [[attribute(0)]];
    float3 color    [[attribute(1)]];
};

struct TriangleVOut {
    float4 position [[position]];
    float3 color;
};

vertex TriangleVOut triangle_vs(
        uint vid [[vertex_id]],
        const device float3* positions [[buffer(0)]],
        const device float3* colors    [[buffer(1)]])
{
    TriangleVOut o;
    // The CPU side packs [pos; color] interleaved in a single buffer. Decoding
    // here would require a stride. We keep two parallel buffers for clarity.
    o.position = float4(positions[vid], 1.0);
    o.color    = colors[vid];
    return o;
}

fragment float4 triangle_fs(TriangleVOut in [[stage_in]])
{
    return float4(in.color, 1.0);
}
