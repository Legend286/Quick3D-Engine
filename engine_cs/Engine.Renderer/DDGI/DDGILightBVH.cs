// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Engine.Renderer.DDGI;

/// <summary>
/// CPU-side binary BVH over punctual light bounding spheres. Built once
/// per frame from the scene light set and uploaded to the GPU so the
/// probe-update compute kernel can resolve per-probe contributing lights
/// in O(log N) instead of O(N). Flat-array layout matches the structured
/// buffer consumed by the compute shader.
/// </summary>
/// <remarks>
/// Per the hybrid light-tree design, this BVH handles punctual lights
/// only. Emissive-surface sampling reuses the Forward+ cluster-grid
/// record atlas (see <see cref="Engine.Renderer.PbrPass"/>) for
/// triangular surface contribution. The choice keeps the BVH small
/// (N = light count) while still letting probes see every emissive
/// cluster that overlaps their position.
/// </remarks>
public sealed class DDGILightBVH
{
    public readonly record struct BoundingSphere(
        Vector3 Center,
        float Radius);

    public readonly record struct Node(
        Vector3 BoundsMin,
        Vector3 BoundsMax,
        int Left,
        int Right,
        int LeafLightIndex,
        int Padding);

    /// <summary>Symmetric sentinel: an internal node stores -1 for LeafLightIndex.</summary>
    public const int LeafLightNone = -1;

    /// <summary>CPU sentinel: LeftAndRight are -1 in leaves.</summary>
    public const int ChildLeaf = -1;

    public readonly record struct PunctualLightSnapshot(
        int Id,
        Vector3 Position,
        float Range,
        float ConeHalfAngleRadians,
        Vector3 ConeDirection);

    private readonly List<Node> _nodes = new();
    private readonly List<int> _lightOrder = new();
    private int _rootIndex = -1;

    public int NodeCount => _nodes.Count;
    public int LightCount => _lightOrder.Count;
    public int RootIndex => _rootIndex;
    public IReadOnlyList<Node> Nodes => _nodes;
    public IReadOnlyList<int> LightOrder => _lightOrder;

    /// <summary>Concrete bounding sphere for a punctual light (used during probe inject).</summary>
    public BoundingSphere GetBoundingSphere(int orderedLightIndex)
    {
        if (orderedLightIndex < 0 || orderedLightIndex >= _lightOrder.Count)
            throw new ArgumentOutOfRangeException(nameof(orderedLightIndex));
        int originalLightId = _lightOrder[orderedLightIndex];
        return _boundingSpheres[originalLightId];
    }

    private readonly Dictionary<int, BoundingSphere> _boundingSpheres = new();

    /// <summary>Builds the BVH from <paramref name="lights"/>. Replaces prior state.</summary>
    public void Build(IReadOnlyList<PunctualLightSnapshot> lights)
    {
        _nodes.Clear();
        _lightOrder.Clear();
        _boundingSpheres.Clear();
        _rootIndex = -1;
        if (lights.Count == 0)
            return;

        for (int i = 0; i < lights.Count; ++i)
        {
            BoundingSphere bounds = ComputeLightBounds(lights[i]);
            _boundingSpheres[lights[i].Id] = bounds;
        }

        var sortedIndices = new int[lights.Count];
        for (int i = 0; i < lights.Count; ++i)
            sortedIndices[i] = i;
        Array.Sort(sortedIndices,
            (a, b) => lights[a].Position.X.CompareTo(lights[b].Position.X));

        _rootIndex = BuildInternal(sortedIndices, 0, sortedIndices.Length - 1, lights);
    }

    private int BuildInternal(
        int[] sortedIndices,
        int from,
        int to,
        IReadOnlyList<PunctualLightSnapshot> lights)
    {
        if (from > to)
            return ChildLeaf;

        Vector3 min = Vector3.One * float.PositiveInfinity;
        Vector3 max = Vector3.One * float.NegativeInfinity;
        for (int i = from; i <= to; ++i)
        {
            BoundingSphere bounds =
                _boundingSpheres[lights[sortedIndices[i]].Id];
            min = Vector3.Min(min, bounds.Center - new Vector3(bounds.Radius));
            max = Vector3.Max(max, bounds.Center + new Vector3(bounds.Radius));
        }

        int nodeIndex = _nodes.Count;
        _nodes.Add(new Node(min, max, ChildLeaf, ChildLeaf, LeafLightNone, 0));

        if (to - from <= 0)
        {
            // Leaf — store light order index for stable per-iteration lookup.
            int lightOrderIndex = _lightOrder.Count;
            _lightOrder.Add(sortedIndices[from]);
            _nodes[nodeIndex] = new Node(
                min, max,
                ChildLeaf, ChildLeaf,
                lightOrderIndex, 0);
            return nodeIndex;
        }

        // Median-split on the dominant axis. Exhaustively picking the
        // largest-extent axis per node is left as a follow-up if
        // light-positions become uneven; for the canonical clustered
        // scene shapes the median split on X is already cheap and
        // depth-balanced.
        int mid = (from + to) / 2;
        int left = BuildInternal(sortedIndices, from, mid, lights);
        int right = BuildInternal(sortedIndices, mid + 1, to, lights);
        _nodes[nodeIndex] = new Node(min, max, left, right, LeafLightNone, 0);
        return nodeIndex;
    }

    private static BoundingSphere ComputeLightBounds(PunctualLightSnapshot light)
    {
        // Standard fits: a directional light has effectively infinite
        // range — collapse its bounding sphere to a fixed epsilon offset
        // (large enough to cover any plausible probe position) so the
        // tree-balancing midpoint-split keeps the directional proxy in
        // place. A point light is a literal sphere with its given Range.
        // A spot light uses its ConeHalfAngleRadians to derive a tight
        // bounding sphere (r = Range / (2 cos(half-angle))) so the BVH
        // doesn't fail to find a probe that's clearly inside the cone.
        //
        // KNOWN COST (Phase-1 follow-up): every directional light gets a
        // 10^6-meter bounding sphere, which makes every other directional
        // sphere fully overlap. The compute kernel then has to traverse
        // both branches even when the probe position is far from any
        // directional. Two cheap mitigations once profiling warrants:
        //   1) split directional lights into a separate flat array,
        //      bypassing the BVH entirely; or
        //   2) shrink the directional sphere radius to a SkyDome-style
        //      cover and treat the per-probe lookup as a virtual-source
        //      term rather than a per-light traversal.
        if (light.Range <= 0.0f)
        {
            return new BoundingSphere(light.Position, 1.0e6f);
        }
        if (light.ConeHalfAngleRadians <= 0.001f)
        {
            return new BoundingSphere(light.Position, light.Range);
        }
        float tightRadius =
            light.Range * 0.5f /
            MathF.Max(MathF.Cos(light.ConeHalfAngleRadians), 0.1f);
        tightRadius = MathF.Min(tightRadius, light.Range);
        return new BoundingSphere(light.Position + light.ConeDirection *
            (tightRadius * 0.5f), tightRadius);
    }
}
