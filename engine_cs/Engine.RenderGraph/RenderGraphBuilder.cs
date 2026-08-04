// SPDX-License-Identifier: MIT
// Render-graph resource builder.

using System;
using System.Collections.Generic;

namespace Engine.RenderGraph;

public sealed class RenderGraphBuilder
{
    internal const uint TransientResourceBase = 0x01000000;
    private uint _nextId = 1;
    private readonly Dictionary<ResourceHandle, ResourceDecl> _decls = new();
    private readonly List<AccessDecl> _thisPassAccesses = new();

    public ResourceHandle CreateTexture(TextureDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc);
        ResourceHandle handle = AllocateHandle();
        _decls[handle] = new ResourceDecl
        {
            Handle = handle,
            Kind = ResourceKind.Texture,
            Texture = desc,
        };
        return handle;
    }

    public ResourceHandle CreateBuffer(BufferDesc desc)
    {
        ArgumentNullException.ThrowIfNull(desc);
        ResourceHandle handle = AllocateHandle();
        _decls[handle] = new ResourceDecl
        {
            Handle = handle,
            Kind = ResourceKind.Buffer,
            Buffer = desc,
        };
        return handle;
    }

    /// <summary>Declares a persistent buffer owned outside the graph.
    /// The executor must bind the matching RHI buffer before execution;
    /// imported resources participate in barriers but never transient
    /// heap aliasing.</summary>
    public void ImportBuffer(ResourceHandle handle)
    {
        ImportBuffer(handle, null);
    }

    /// <summary>Declares a persistent buffer with an optional descriptor.</summary>
    public void ImportBuffer(ResourceHandle handle, BufferDesc? desc)
    {
        if (!handle.IsValid)
            throw new ArgumentException(
                "External resource handle must be valid.",
                nameof(handle));
        RejectTransientNamespace(handle);
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
            throw new ArgumentException(
                "External resource handle must be valid.",
                nameof(handle));
        RejectTransientNamespace(handle);
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

    public void Read(ResourceHandle handle, ResourceState state)
    {
        ValidateAccessHandle(handle);
        _thisPassAccesses.Add(new AccessDecl(handle, ResourceAccess.Read, state));
    }

    public void Write(ResourceHandle handle, ResourceState state)
    {
        ValidateAccessHandle(handle);
        _thisPassAccesses.Add(new AccessDecl(handle, ResourceAccess.Write, state));
    }

    public void ReadWrite(ResourceHandle handle, ResourceState state)
    {
        ValidateAccessHandle(handle);
        _thisPassAccesses.Add(new AccessDecl(handle, ResourceAccess.ReadWrite, state));
    }

    /// <summary>Move the builder's accumulated per-pass access declarations
    /// into the destination list, then reset the builder for the next pass.</summary>
    internal void DrainPassAccesses(List<AccessDecl> destination)
    {
        destination.AddRange(_thisPassAccesses);
        _thisPassAccesses.Clear();
    }

    /// <summary>Returns a defensive declaration snapshot for compilation.</summary>
    internal IReadOnlyDictionary<ResourceHandle, ResourceDecl> SnapshotDeclarations()
    {
        var snapshot = new Dictionary<ResourceHandle, ResourceDecl>(_decls.Count);
        foreach ((ResourceHandle handle, ResourceDecl declaration) in _decls)
        {
            snapshot.Add(handle, CloneDeclaration(declaration, handle));
        }
        return snapshot;
    }

    private ResourceHandle AllocateHandle()
    {
        while (_nextId == 0 || _decls.ContainsKey(new ResourceHandle(_nextId)))
        {
            if (_nextId == uint.MaxValue)
                throw new InvalidOperationException(
                    "Render graph resource handle space is exhausted.");
            _nextId++;
        }

        uint id = checked(TransientResourceBase + _nextId++);
        while (_decls.ContainsKey(new ResourceHandle(id)))
        {
            id = checked(TransientResourceBase + _nextId++);
        }
        return new ResourceHandle(id);
    }

    private void ValidateAccessHandle(ResourceHandle handle)
    {
        if (!handle.IsValid)
            throw new ArgumentException(
                "Resource handle must be valid.",
                nameof(handle));
        if (handle.Id >= TransientResourceBase &&
            handle.Id < TransientResourceBase + 0x01000000u &&
            !_decls.ContainsKey(handle))
        {
            throw new ArgumentException(
                $"Transient resource handle 0x{handle.Id:X8} was not created by this graph.",
                nameof(handle));
        }
    }

    private static void RejectTransientNamespace(ResourceHandle handle)
    {
        if (handle.Id >= TransientResourceBase &&
            handle.Id < TransientResourceBase + 0x01000000u)
        {
            throw new ArgumentException(
                $"External resource handle 0x{handle.Id:X8} is in the reserved transient namespace.",
                nameof(handle));
        }
    }

    internal static ResourceDecl CloneDeclaration(
        ResourceDecl declaration,
        ResourceHandle handle)
        => new()
        {
            Handle = handle,
            Kind = declaration.Kind,
            Texture = declaration.Texture == null
                ? null
                : new TextureDesc(
                    declaration.Texture.Width,
                    declaration.Texture.Height)
                {
                    MipLevels = declaration.Texture.MipLevels,
                    Format = declaration.Texture.Format,
                    UsageFlags = declaration.Texture.UsageFlags,
                },
            Buffer = declaration.Buffer == null
                ? null
                : new BufferDesc
                {
                    Size = declaration.Buffer.Size,
                    Usage = declaration.Buffer.Usage,
                },
            External = declaration.External,
        };
}

public sealed class ResourceDecl
{
    public ResourceHandle Handle { get; init; }
    public ResourceKind Kind { get; init; }
    public TextureDesc? Texture { get; init; }
    public BufferDesc? Buffer { get; init; }
    public bool External { get; init; }
}

public sealed record AccessDecl(
    ResourceHandle Resource,
    ResourceAccess Access,
    ResourceState State);
