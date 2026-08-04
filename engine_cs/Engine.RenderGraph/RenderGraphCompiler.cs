// SPDX-License-Identifier: MIT
// Render graph declaration analysis and immutable plan templates.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Engine.CBindings;
using Engine.RHI;

namespace Engine.RenderGraph;

/// <summary>Compiles pass declarations into reusable immutable graph templates.
/// Pass setup remains caller-thread work; cached analysis is independent of
/// native RHI objects and is suitable for later background compilation.</summary>
public sealed class RenderGraphCompiler
{
    private const int MaximumCachedTemplates = 32;
    private readonly Dictionary<string, RenderGraphTemplate> _templateCache = new();
    private readonly LinkedList<string> _cacheLru = new();

    /// <summary>Number of immutable templates retained by this compiler.</summary>
    public int CachedTemplateCount => _templateCache.Count;

    /// <summary>Removes all cached declaration-analysis templates.</summary>
    public void ClearCache()
    {
        _templateCache.Clear();
        _cacheLru.Clear();
    }

    /// <summary>Compiles pass declarations and reuses matching immutable analysis.</summary>
    public RenderPlan Compile(IReadOnlyList<RenderPass> passes)
    {
        ArgumentNullException.ThrowIfNull(passes);
        var builder = new RenderGraphBuilder();
        var passAccesses = new List<List<AccessDecl>>(passes.Count);
        for (int passIndex = 0; passIndex < passes.Count; ++passIndex)
        {
            var accesses = new List<AccessDecl>();
            passes[passIndex].Setup(builder);
            builder.DrainPassAccesses(accesses);
            passAccesses.Add(accesses);
        }

        IReadOnlyDictionary<ResourceHandle, ResourceDecl> declarations =
            builder.SnapshotDeclarations();
        string signature = BuildSignature(passes, declarations, passAccesses);
        if (_templateCache.TryGetValue(signature, out RenderGraphTemplate? template))
        {
            _cacheLru.Remove(signature);
            _cacheLru.AddLast(signature);
        }
        else
        {
            template = BuildTemplate(declarations, passAccesses);
            AddTemplate(signature, template);
        }

        return new RenderPlan
        {
            Template = template,
            Passes = passes.ToArray(),
            PassAccesses = template.PassAccesses
                .Select(accesses => accesses.ToList())
                .ToArray(),
            BarriersPerPass = template.BarriersPerPass
                .Select(barriers => barriers.ToList())
                .ToArray(),
        };
    }

    private void AddTemplate(string signature, RenderGraphTemplate template)
    {
        _templateCache[signature] = template;
        _cacheLru.Remove(signature);
        _cacheLru.AddLast(signature);
        while (_cacheLru.Count > MaximumCachedTemplates)
        {
            LinkedListNode<string> expired = _cacheLru.First!;
            _cacheLru.RemoveFirst();
            _templateCache.Remove(expired.Value);
        }
    }

    private static RenderGraphTemplate BuildTemplate(
        IReadOnlyDictionary<ResourceHandle, ResourceDecl> declarations,
        IReadOnlyList<List<AccessDecl>> passAccesses)
    {
        var finalStates = ComputeFinalStates(declarations, passAccesses);
        var barrierLists = new List<List<BarrierDecl>>(passAccesses.Count);
        foreach (List<AccessDecl> _ in passAccesses)
            barrierLists.Add(new List<BarrierDecl>());

        var currentStates = declarations.Keys.ToDictionary(
            handle => handle,
            _ => ResourceState.Undefined);
        var currentAccesses = declarations.Keys.ToDictionary(
            handle => handle,
            _ => (ResourceAccess?)null);
        for (int passIndex = 0; passIndex < passAccesses.Count; ++passIndex)
        {
            foreach (AccessDecl access in passAccesses[passIndex])
            {
                ResourceState prior = currentStates.GetValueOrDefault(
                    access.Resource,
                    ResourceState.Undefined);
                ResourceAccess? priorAccess = currentAccesses.GetValueOrDefault(
                    access.Resource);
                bool stateChanged = prior != access.State;
                bool writeHazard =
                    priorAccess is ResourceAccess.Write or ResourceAccess.ReadWrite ||
                    access.Access is ResourceAccess.Write or ResourceAccess.ReadWrite;
                if (stateChanged || writeHazard && priorAccess != null)
                {
                    barrierLists[passIndex].Add(
                        new BarrierDecl(
                            access.Resource,
                            prior,
                            access.State));
                }
                currentStates[access.Resource] = access.State;
                currentAccesses[access.Resource] = access.Access;
            }
        }

        MemoryAliasingPlan aliasing = ComputeAliasing(
            declarations,
            passAccesses);
        var readOnlyAccesses = new IReadOnlyList<AccessDecl>[passAccesses.Count];
        var readOnlyBarriers = new IReadOnlyList<BarrierDecl>[barrierLists.Count];
        for (int index = 0; index < passAccesses.Count; ++index)
        {
            readOnlyAccesses[index] = new ReadOnlyCollection<AccessDecl>(
                passAccesses[index].ToArray());
            readOnlyBarriers[index] = new ReadOnlyCollection<BarrierDecl>(
                barrierLists[index].ToArray());
        }

        return new RenderGraphTemplate
        {
            ResourceDecls = new ReadOnlyDictionary<ResourceHandle, ResourceDecl>(
                new Dictionary<ResourceHandle, ResourceDecl>(declarations)),
            PassAccesses = readOnlyAccesses,
            BarriersPerPass = readOnlyBarriers,
            FinalStates = new ReadOnlyDictionary<ResourceHandle, ResourceState>(
                finalStates),
            Aliasing = aliasing,
        };
    }

    private static Dictionary<ResourceHandle, ResourceState> ComputeFinalStates(
        IReadOnlyDictionary<ResourceHandle, ResourceDecl> declarations,
        IReadOnlyList<List<AccessDecl>> passAccesses)
    {
        var final = new Dictionary<ResourceHandle, ResourceState>();
        foreach (ResourceHandle handle in declarations.Keys)
        {
            ResourceState lastWrite = ResourceState.Undefined;
            ResourceState lastSeen = ResourceState.Undefined;
            foreach (List<AccessDecl> accesses in passAccesses)
            {
                foreach (AccessDecl access in accesses)
                {
                    if (access.Resource != handle)
                        continue;
                    lastSeen = access.State;
                    if (access.Access is ResourceAccess.Write or ResourceAccess.ReadWrite)
                        lastWrite = access.State;
                }
            }
            final[handle] = lastWrite != ResourceState.Undefined
                ? lastWrite
                : lastSeen;
        }
        return final;
    }

    private static MemoryAliasingPlan ComputeAliasing(
        IReadOnlyDictionary<ResourceHandle, ResourceDecl> declarations,
        IReadOnlyList<List<AccessDecl>> passAccesses)
    {
        var lifespans = new Dictionary<ResourceHandle, (int Start, int End)>();
        for (int passIndex = 0; passIndex < passAccesses.Count; ++passIndex)
        {
            foreach (AccessDecl access in passAccesses[passIndex])
            {
                if (lifespans.TryGetValue(access.Resource, out var span))
                    lifespans[access.Resource] = (span.Start, passIndex);
                else
                    lifespans[access.Resource] = (passIndex, passIndex);
            }
        }

        ulong totalHeapSize = 0;
        var offsets = new Dictionary<ResourceHandle, ulong>();
        var active = new List<(
            ResourceHandle Handle,
            ulong Offset,
            ulong Size,
            int End)>();
        foreach ((ResourceHandle handle, ResourceDecl declaration) in declarations)
        {
            if (declaration.External || !lifespans.TryGetValue(handle, out var span))
                continue;

            ulong size = GetResourceSize(declaration);
            active.RemoveAll(resource => resource.End < span.Start);
            ulong offset = 0;
            foreach (var resource in active)
                offset = Math.Max(offset, resource.Offset + resource.Size);
            offset = (offset + 65535) & ~65535ul;
            offsets[handle] = offset;
            active.Add((handle, offset, size, span.End));
            totalHeapSize = Math.Max(totalHeapSize, offset + size);
        }

        return new MemoryAliasingPlan
        {
            TotalHeapSize = totalHeapSize,
            ResourceOffsets = new ReadOnlyDictionary<ResourceHandle, ulong>(offsets),
        };
    }

    private static ulong GetResourceSize(ResourceDecl declaration)
    {
        if (declaration.Kind == ResourceKind.Buffer)
            return declaration.Buffer?.Size ?? 0;
        if (declaration.Kind == ResourceKind.Texture && declaration.Texture != null)
        {
            ulong bytesPerPixel = RhiTexture.GetUncompressedBytesPerPixel(
                declaration.Texture.Format);
            if (bytesPerPixel == 0)
                bytesPerPixel = 4;
            return (ulong)declaration.Texture.Width *
                declaration.Texture.Height *
                bytesPerPixel;
        }
        return 0;
    }

    private static string BuildSignature(
        IReadOnlyList<RenderPass> passes,
        IReadOnlyDictionary<ResourceHandle, ResourceDecl> declarations,
        IReadOnlyList<List<AccessDecl>> passAccesses)
    {
        var text = new StringBuilder();
        // External handles are part of graph identity because they identify
        // caller-owned RHI resources. Builder-created handles are graph-local
        // and deterministic, so they do not vary between equivalent compiles.
        text.Append("render-graph-v2|").Append(passes.Count).Append('|');
        foreach (RenderPass pass in passes)
        {
            text.Append(pass.GetType().FullName).Append('|')
                .Append(pass.Name).Append('|')
                .Append((int)pass.Queue).Append(';');
        }

        foreach ((ResourceHandle handle, ResourceDecl declaration) in declarations
                     .OrderBy(entry => entry.Key.Id))
        {
            text.Append("r:").Append(handle.Id).Append(':')
                .Append((int)declaration.Kind).Append(':')
                .Append(declaration.External ? '1' : '0').Append(':');
            if (declaration.Texture is TextureDesc texture)
            {
                text.Append('t').Append(texture.Width).Append(',')
                    .Append(texture.Height).Append(',')
                    .Append(texture.MipLevels).Append(',')
                    .Append((int)texture.Format).Append(',')
                    .Append(texture.UsageFlags);
            }
            else if (declaration.Buffer is BufferDesc buffer)
            {
                text.Append('b').Append(buffer.Size).Append(',')
                    .Append((uint)buffer.Usage);
            }
            text.Append(';');
        }

        for (int passIndex = 0; passIndex < passAccesses.Count; ++passIndex)
        {
            text.Append("p:").Append(passIndex).Append(':');
            foreach (AccessDecl access in passAccesses[passIndex])
            {
                text.Append(access.Resource.Id).Append(',')
                    .Append((int)access.Access).Append(',')
                    .Append((int)access.State).Append(';');
            }
        }

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return Convert.ToHexString(digest);
    }
}

/// <summary>Immutable declaration-analysis result reusable by multiple
/// render-plan instances that have the same graph signature.</summary>
public sealed class RenderGraphTemplate
{
    public required IReadOnlyDictionary<ResourceHandle, ResourceDecl> ResourceDecls { get; init; }
    public required IReadOnlyList<IReadOnlyList<AccessDecl>> PassAccesses { get; init; }
    public required IReadOnlyList<IReadOnlyList<BarrierDecl>> BarriersPerPass { get; init; }
    public required IReadOnlyDictionary<ResourceHandle, ResourceState> FinalStates { get; init; }
    public required MemoryAliasingPlan Aliasing { get; init; }
}

public sealed class RenderPlan
{
    public required RenderGraphTemplate Template { get; init; }
    public required RenderPass[] Passes { get; init; }
    public required IReadOnlyList<List<AccessDecl>> PassAccesses { get; init; }
    public required IReadOnlyList<List<BarrierDecl>> BarriersPerPass { get; init; }
    public IReadOnlyDictionary<ResourceHandle, ResourceDecl> ResourceDecls => Template.ResourceDecls;
    public IReadOnlyDictionary<ResourceHandle, ResourceState> FinalStates => Template.FinalStates;
    public MemoryAliasingPlan Aliasing => Template.Aliasing;
}

public sealed record BarrierDecl(
    ResourceHandle Resource,
    ResourceState StateBefore,
    ResourceState StateAfter);
