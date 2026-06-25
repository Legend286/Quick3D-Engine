// SPDX-License-Identifier: MIT
// Render-graph resource builder.
//
// The id counter is process-wide so every resource declaration across every
// RenderGraph compile session has a globally unique handle. Per-pass
// builders used to allocate ids from a fresh `_nextId = 1` which meant pass
// 0's vertex buffer and pass 1's vertex buffer had the same handle, breaking
// barrier inference. The shared counter fixes that.

using System.Collections.Generic;

namespace Engine.RenderGraph;

public sealed class RenderGraphBuilder
{
    // Shared across every builder instance, regardless of pass ownership.
    private static uint _sharedNextId = 1;

    private readonly Dictionary<ResourceHandle, ResourceDecl> _decls = new();
    private readonly List<AccessDecl> _thisPassAccesses = new();

    public ResourceHandle CreateTexture(TextureDesc desc)
    {
        var h = new ResourceHandle(_sharedNextId++);
        _decls[h] = new ResourceDecl
        {
            Handle = h,
            Kind = ResourceKind.Texture,
            Texture = desc,
        };
        return h;
    }

    public ResourceHandle CreateBuffer(BufferDesc desc)
    {
        var h = new ResourceHandle(_sharedNextId++);
        _decls[h] = new ResourceDecl
        {
            Handle = h,
            Kind = ResourceKind.Buffer,
            Buffer = desc,
        };
        return h;
    }

    public void Read(ResourceHandle h, ResourceState state) =>
        _thisPassAccesses.Add(new AccessDecl(h, ResourceAccess.Read, state));
    public void Write(ResourceHandle h, ResourceState state) =>
        _thisPassAccesses.Add(new AccessDecl(h, ResourceAccess.Write, state));
    public void ReadWrite(ResourceHandle h, ResourceState state) =>
        _thisPassAccesses.Add(new AccessDecl(h, ResourceAccess.ReadWrite, state));

    /// <summary>Move the builder's accumulated per-pass access decls into the
    /// destination list, then reset the builder for the next pass. Used by
    /// <see cref="RenderGraphCompiler"/> when calling Setup on multiple passes
    /// against one shared builder.</summary>
    internal void DrainPassAccesses(List<AccessDecl> destination)
    {
        destination.AddRange(_thisPassAccesses);
        _thisPassAccesses.Clear();
    }

    /// <summary>Internal: snapshot declarations for the compile pass.</summary>
    internal IReadOnlyDictionary<ResourceHandle, ResourceDecl> DeclaredResources => _decls;
    internal IReadOnlyList<AccessDecl> PassAccesses => _thisPassAccesses;
}

internal sealed class ResourceDecl
{
    public ResourceHandle Handle;
    public ResourceKind Kind;
    public TextureDesc? Texture;
    public BufferDesc?  Buffer;
}

internal sealed record AccessDecl(ResourceHandle Resource,
                                  ResourceAccess Access,
                                  ResourceState State);
