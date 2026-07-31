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

    /// <summary>Declares a persistent buffer owned outside the graph.
    /// The executor must bind the matching RHI buffer before execution;
    /// imported resources participate in barriers but never transient
    /// heap aliasing.</summary>
    /// <summary>Declares a persistent buffer without requiring a transient
    /// descriptor. The matching RHI buffer is bound by the executor.</summary>
    public void ImportBuffer(ResourceHandle handle)
    {
        ImportBuffer(handle, null);
    }

    /// <summary>Declares a persistent buffer with an optional descriptor.</summary>
    public void ImportBuffer(ResourceHandle handle, BufferDesc? desc)
    {
        if (!handle.IsValid)
            throw new ArgumentException("External resource handle must be valid.", nameof(handle));
        if (_decls.TryGetValue(handle, out ResourceDecl? existing))
        {
            if (existing.Kind != ResourceKind.Buffer || !existing.External)
                throw new InvalidOperationException(
                    $"Resource handle 0x{handle.Id:X8} was already declared as a non-external resource.");
            return;
        }
        _decls[handle] = new ResourceDecl
        {
            Handle = handle,
            Kind = ResourceKind.Buffer,
            Buffer = desc,
            External = true,
        };
    }

    /// <summary>Declares a persistent texture owned outside the graph.
    /// The executor must bind the matching RHI texture before execution;
    /// imported resources participate in barriers but never transient
    /// heap aliasing.</summary>
    public void ImportTexture(ResourceHandle handle)
    {
        ImportTexture(handle, null);
    }

    /// <summary>Declares a persistent texture with an optional descriptor.
    /// External resources are never allocated by the graph, so a descriptor
    /// is only needed when tooling wants to inspect the declaration.</summary>
    public void ImportTexture(ResourceHandle handle, TextureDesc? desc)
    {
        if (!handle.IsValid)
            throw new ArgumentException("External resource handle must be valid.", nameof(handle));
        if (_decls.TryGetValue(handle, out ResourceDecl? existing))
        {
            if (existing.Kind != ResourceKind.Texture || !existing.External)
                throw new InvalidOperationException(
                    $"Resource handle 0x{handle.Id:X8} was already declared as a non-external resource.");
            return;
        }
        _decls[handle] = new ResourceDecl
        {
            Handle = handle,
            Kind = ResourceKind.Texture,
            Texture = desc,
            External = true,
        };
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

// ResourceDecl + AccessDecl are surfaced through the public `RenderGraph`
// aggregate (RenderGraphCompiler.cs). Their visibility must match — keeping
// them `internal` makes the public properties return a less-accessible type
// (compiler error CS0053). They are plain data carriers; future refactors
// can move them into a dedicated types/records file.
public sealed class ResourceDecl
{
    public ResourceHandle Handle;
    public ResourceKind Kind;
    public TextureDesc? Texture;
    public BufferDesc?  Buffer;
    public bool External;
}

public sealed record AccessDecl(ResourceHandle Resource,
                                ResourceAccess Access,
                                ResourceState State);
