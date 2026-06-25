// SPDX-License-Identifier: MIT
// Render graph compiler. Walks pass declarations in registration order, builds
// per-resource access timelines, infers barrier points between consecutive
// accesses whose state diverges.
//
// Algorithm (MVP1):
//   1. One RENDERGRAPHBUILDER is shared across the entire Compile() call.
//      This ensures resource handles minted in pass.Setup are GRAFH-GLOBAL,
//      so barrier inference sees every pass's access on the same key.
//
//   2. Each pass.Setup() runs and drains its access decls into a per-pass
//      list. This keeps the "current pass" view intact while also preserving
//      the historical access timeline for the same resource.
//
//   3. For each (resource) all accesses are walked in registration order.
//      Between consecutive accesses whose state diverges, a BarrierDecl is
//      emitted BEFORE the later access. Metal treats extra barriers as
//      no-ops; Phase 4 Vulkan will route them through rhi_cmd_pipeline_barrier.
//
// Limitations deferred to post-MVP1: async compute, parallel barriers,
// transient memory aliasing, culling of unused passes, automatic resolve +
// depth transitions.

using System.Collections.Generic;
using System.Linq;

namespace Engine.RenderGraph;

public sealed class RenderGraphCompiler
{
    public RenderPlan Compile(IReadOnlyList<RenderPass> passes)
    {
        var sharedBuilder = new RenderGraphBuilder();

        var passAccesses = new List<List<AccessDecl>>(passes.Count);
        for (int i = 0; i < passes.Count; ++i)
        {
            var list = new List<AccessDecl>();
            passes[i].Setup(sharedBuilder);
            sharedBuilder.DrainPassAccesses(list);
            passAccesses.Add(list);
        }

        var resourceDecls = sharedBuilder.DeclaredResources;

        // Compute the final declared-state of each resource: the state of its
        // last Write (or ReadWrite).
        var finalStates = ComputeFinalStates(resourceDecls, passAccesses);

        // Forward propagation barrier inference.
        var barrierLists = new List<List<BarrierDecl>>(passes.Count);
        foreach (var _ in passes) barrierLists.Add(new List<BarrierDecl>());

        var currentStates = resourceDecls.ToDictionary(
            kv => kv.Key, kv => ResourceState.Undefined);

        for (int i = 0; i < passes.Count; ++i)
        {
            var list = barrierLists[i];
            foreach (var access in passAccesses[i])
            {
                var prior = currentStates.GetValueOrDefault(access.Resource, ResourceState.Undefined);
                if (prior != access.State)
                {
                    list.Add(new BarrierDecl(access.Resource, prior, access.State));
                }
                // Writes and ReadWrite advances the resource state. Reads do
                // NOT mutate "current state" because the resource is still
                // (semantically) in whatever state the last Write left it.
                if (access.Access == ResourceAccess.Write
                 || access.Access == ResourceAccess.ReadWrite)
                {
                    currentStates[access.Resource] = access.State;
                }
            }
        }

        return new RenderPlan
        {
            ResourceDecls = resourceDecls,
            Passes = passes.ToArray(),
            PassAccesses = passAccesses,
            BarriersPerPass = barrierLists,
            FinalStates = finalStates,
        };
    }

    private static Dictionary<ResourceHandle, ResourceState> ComputeFinalStates(
        IReadOnlyDictionary<ResourceHandle, ResourceDecl> decls,
        IReadOnlyList<List<AccessDecl>> perPass)
    {
        var final = new Dictionary<ResourceHandle, ResourceState>();
        foreach (var (h, decl) in decls)
        {
            ResourceState lastWrite = ResourceState.Undefined;
            ResourceState lastSeen = ResourceState.Undefined;
            foreach (var list in perPass)
                foreach (var a in list)
                {
                    if (a.Resource != h) continue;
                    lastSeen = a.State;
                    if (a.Access == ResourceAccess.Write
                     || a.Access == ResourceAccess.ReadWrite)
                        lastWrite = a.State;
                }
            final[h] = lastWrite != ResourceState.Undefined ? lastWrite : lastSeen;
        }
        return final;
    }
}

public sealed class RenderPlan
{
    public required IReadOnlyDictionary<ResourceHandle, ResourceDecl> ResourceDecls { get; init; }
    public required RenderPass[] Passes { get; init; }
    public required IReadOnlyList<List<AccessDecl>> PassAccesses { get; init; }
    public required IReadOnlyList<List<BarrierDecl>> BarriersPerPass { get; init; }
    public required IReadOnlyDictionary<ResourceHandle, ResourceState> FinalStates { get; init; }
}

public sealed record BarrierDecl(ResourceHandle Resource,
                                 ResourceState StateBefore,
                                 ResourceState StateAfter);
