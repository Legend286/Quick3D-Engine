// SPDX-License-Identifier: MIT
using System.Numerics;
using System.Runtime.InteropServices;

namespace Engine.DDGI;

/// <summary>
/// 32-byte BBV light-tree node descriptor used by the DDGI
/// probe-update kernel's hierarchical light gather.
///
/// Packing (matches <see cref="shaders/ddgi_probe_update.slang"/>
/// LightTreeNode):
///   * <see cref="MinData0"/>: xyz = node AABB min,
///     w = asfloat(childLeft index) when internal, OR
///     asfloat(firstLightIndex | 0x80000000) when leaf.
///   * <see cref="MaxData1"/>: xyz = node AABB max,
///     w = asfloat(childRight index) when internal, OR
///     asfloat(lightCount) when leaf.
///
/// The high bit of <see cref="MinData0"/>.w acts as the leaf flag,
/// keeping the C# struct free of helper accessor noise and letting
/// the shader test leaf-ness with a single unsigned mask.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DDGILightTreeNode
{
    public Vector4 MinData0;
    public Vector4 MaxData1;

    public const uint LeafBit = 0x80000000u;
    public const uint LeafMask = 0x7FFFFFFFu;
}
