// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Engine.DDGI;

/// <summary>
/// CPU-side BBV (Bounding-Box Volume) builder for the DDGI light
/// tree. Walks the input <see cref="DDGILightSnapshot"/> set top-down
/// with a principal-axis split on light centroids and packs ≤
/// <see cref="LeafLightBudget"/> lights into each leaf. The
/// snapshot array is reordered in-place so each leaf references a
/// contiguous range in the Lights SSBO; the shader traversal reads
/// the same packed order, so no separate remapping buffer is
/// needed.
///
/// Heuristic: split on the longest centroid extent axis at the
/// median light position. Directional lights carry an effectively
/// infinite range (Position.W = 1e6 m) so their AABB acts as a
/// scene-wide "always-include" stamp that's pruned at the leaf
/// level by the shader cone/range tests.
///
/// Tree depth at 128 lights with LeafLightBudget = 4 typically
/// saturates around depth 6-7 — well within direct-recursion call
/// stack limits — so the builder uses straight recursion rather
/// than a manual worklist.
/// </summary>
public sealed class DDGILightTreeBuilder
{
    /// <summary>Max lights packed into a leaf. Smaller values
    /// produce deeper trees with finer spatial pruning; larger
    /// values flatten the tree but increase per-leaf
    /// contribution cost. Frostbite / SEED converge on 4.</summary>
    public int LeafLightBudget { get; set; } = 4;

    public DDGILightTreeNode[] BuildCpu(
        Span<DDGILightSnapshot> lights,
        out int rootIndex)
    {
        rootIndex = -1;
        if (lights.Length == 0)
            return Array.Empty<DDGILightTreeNode>();

        var indices = new int[lights.Length];
        for (int i = 0; i < indices.Length; ++i)
            indices[i] = i;

        var centroids = new Vector3[lights.Length];
        for (int i = 0; i < lights.Length; ++i)
        {
            Vector4 p = lights[i].Position;
            centroids[i] = new Vector3(p.X, p.Y, p.Z);
        }

        var nodes = new List<DDGILightTreeNode>(Math.Max(2, lights.Length * 2));
        var reordered = new List<DDGILightSnapshot>(lights.Length);

        BuildNode(
            indices, 0, indices.Length,
            centroids,
            nodes,
            reordered,
            lights);

        for (int i = 0; i < reordered.Count; ++i)
            lights[i] = reordered[i];

        rootIndex = 0;
        return nodes.ToArray();
    }

    private void BuildNode(
        int[] indices,
        int start,
        int end,
        Vector3[] centroids,
        List<DDGILightTreeNode> nodes,
        List<DDGILightSnapshot> reordered,
        Span<DDGILightSnapshot> lights)
    {
        int count = end - start;
        int firstLight = reordered.Count;

        Vector3 aabbMin = new(float.PositiveInfinity);
        Vector3 aabbMax = new(float.NegativeInfinity);
        Vector3 centroidMin = new(float.PositiveInfinity);
        Vector3 centroidMax = new(float.NegativeInfinity);

        for (int k = start; k < end; ++k)
        {
            int lightIdx = indices[k];
            DDGILightSnapshot snap = lights[lightIdx];
            Vector3 c = centroids[lightIdx];
            float range = Math.Max(snap.Position.W, 0.001f);

            Vector3 lmin = c - new Vector3(range);
            Vector3 lmax = c + new Vector3(range);

            aabbMin = Vector3.Min(aabbMin, lmin);
            aabbMax = Vector3.Max(aabbMax, lmax);
            centroidMin = Vector3.Min(centroidMin, c);
            centroidMax = Vector3.Max(centroidMax, c);

            reordered.Add(snap);
        }

        nodes.Add(default);

        if (count <= LeafLightBudget)
        {
            uint leafFirst = (uint)firstLight | DDGILightTreeNode.LeafBit;
            uint leafCount = (uint)count;
            nodes[^1] = new DDGILightTreeNode
            {
                MinData0 = new Vector4(
                    aabbMin.X, aabbMin.Y, aabbMin.Z,
                    BitConverter.UInt32BitsToSingle(leafFirst)),
                MaxData1 = new Vector4(
                    aabbMax.X, aabbMax.Y, aabbMax.Z,
                    BitConverter.UInt32BitsToSingle(leafCount)),
            };
            return;
        }

        int axisLongest = 0;
        Vector3 extent = centroidMax - centroidMin;
        if (extent.Y > extent.X) axisLongest = 1;
        if (extent.Z > extent[axisLongest]) axisLongest = 2;
        float pivotCoord = 0.5f *
            (centroidMin[axisLongest] + centroidMax[axisLongest]);

        int mid = PartitionByAxis(
            indices, start, end, centroids, axisLongest, pivotCoord);
        if (mid == start || mid == end)
            mid = start + count / 2;

        int parentIdx = nodes.Count - 1;
        nodes[parentIdx] = new DDGILightTreeNode
        {
            MinData0 = new Vector4(
                aabbMin.X, aabbMin.Y, aabbMin.Z,
                BitConverter.UInt32BitsToSingle((uint)parentIdx)),
            MaxData1 = new Vector4(
                aabbMax.X, aabbMax.Y, aabbMax.Z,
                BitConverter.UInt32BitsToSingle((uint)parentIdx)),
        };

        BuildNode(indices, start, mid,
            centroids, nodes, reordered, lights);
        int leftChild = parentIdx + 1;

        BuildNode(indices, mid, end,
            centroids, nodes, reordered, lights);
        int rightChild = leftChild + CountSubtreeSize(
            mid - start, LeafLightBudget);

        nodes[parentIdx] = new DDGILightTreeNode
        {
            MinData0 = new Vector4(
                aabbMin.X, aabbMin.Y, aabbMin.Z,
                BitConverter.UInt32BitsToSingle((uint)leftChild)),
            MaxData1 = new Vector4(
                aabbMax.X, aabbMax.Y, aabbMax.Z,
                BitConverter.UInt32BitsToSingle((uint)rightChild)),
        };
    }

    private static int CountSubtreeSize(int lightCount, int leafBudget)
    {
        if (lightCount <= 0) return 1;
        int leaves = (lightCount + leafBudget - 1) / leafBudget;
        int leafSlots = leaves;
        while (leaves > 1)
        {
            leaves = (leaves + 1) / 2;
            leafSlots += leaves;
        }
        return leafSlots;
    }

    private static int PartitionByAxis(
        int[] indices, int start, int end,
        Vector3[] centroids, int axis, float pivot)
    {
        int i = start;
        int j = end - 1;
        while (i <= j)
        {
            while (i <= j && centroids[indices[i]][axis] <= pivot) ++i;
            while (i <= j && centroids[indices[j]][axis] > pivot) --j;
            if (i <= j)
            {
                (indices[i], indices[j]) = (indices[j], indices[i]);
                ++i;
                --j;
            }
        }
        return i;
    }
}
