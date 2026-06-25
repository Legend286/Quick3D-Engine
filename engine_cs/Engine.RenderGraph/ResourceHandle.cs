// SPDX-License-Identifier: MIT
// Resource state + handle types for the render graph.

using System;

namespace Engine.RenderGraph;

public enum ResourceState
{
    Undefined        = 0,
    RenderTarget     = 1,
    DepthStencil     = 2,
    ShaderRead       = 3,
    UnorderedAccess  = 4,
    CopySrc          = 5,
    CopyDst          = 6,
    Present          = 7,
}

public enum ResourceAccess
{
    Read,
    Write,
    ReadWrite,
}

/// <summary>Stable, generation-tagged handle to a render-graph resource.
/// Generation prevents stale references when a slot is reused.</summary>
public readonly record struct ResourceHandle(uint Id)
{
    public static ResourceHandle Invalid => default;
    public bool IsValid => Id != 0;
}

public enum ResourceKind { Texture, Buffer }

public sealed class TextureDesc
{
    public uint Width;
    public uint Height;
    public uint MipLevels = 1;
    public Engine.CBindings.RhiNative.TextureFormat Format =
        Engine.CBindings.RhiNative.TextureFormat.Bgra8Unorm;
    public uint UsageFlags;

    public TextureDesc(uint w, uint h)
    { Width = w; Height = h; }
}

public sealed class BufferDesc
{
    public ulong Size;
    public Engine.CBindings.RhiNative.BufferUsage Usage;
}
