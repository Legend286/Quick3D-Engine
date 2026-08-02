// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Numerics;
using Engine.Assets;
using Engine.CBindings;
using Engine.RHI;

namespace Engine.Renderer;

/// <summary>Owns the frame-local dynamic vertex stream used by compute skinning.</summary>
internal sealed class AnimationFrameContext : IDisposable
{
    private const int FrameBufferCount = 3;
    private const ulong InitialBufferBytes = 1024ul * 1024ul;
    private readonly RhiDevice _device;
    private readonly RhiBuffer?[] _dynamicBuffers = new RhiBuffer?[FrameBufferCount];
    private readonly Dictionary<(ulong EntityId, ulong MeshId), RhiBuffer> _buffers = new();
    private readonly Dictionary<(ulong EntityId, ulong MeshId), ulong> _addresses = new();
    private readonly List<SkinWorkItem> _workItems = new();
    private readonly HashSet<RhiBuffer> _uniqueBuffers = new();
    private readonly List<RhiBuffer> _retiredBuffers = new();
    private ulong _cursor;
    private int _bufferIndex;

    internal sealed class SkinWorkItem
    {
        public required ulong EntityId { get; init; }
        public required ulong MeshId { get; init; }
        public required Mesh Mesh { get; init; }
        public required uint VertexCount { get; init; }
        public required RhiBuffer OutputBuffer { get; init; }
        public required ulong OutputAddress { get; init; }
        public required Vector3 OutputOffset { get; init; }
        public ulong SkinMatricesAddress { get; set; }
    }

    /// <summary>Identity of one skinned vertex stream. Instances sharing a
    /// key produce byte-identical skinned output, so they can share the
    /// stream and the skinning dispatch.</summary>
    private readonly record struct SkinStreamKey(
        ulong MeshId,
        uint SkeletonId,
        uint BaseClipId,
        float PlaybackRate,
        float BaseTime,
        uint AnimatorFlags,
        Vector3 OutputOffset);

    private sealed class SkinGroup
    {
        public required Mesh Mesh { get; init; }
        public required uint VertexCount { get; init; }
        public required Vector3 OutputOffset { get; init; }
        public List<ulong> Members { get; } = new();
    }

    public AnimationFrameContext(RhiDevice device)
    {
        _device = device;
    }

    /// <summary>Clears prior addresses and preallocates all current-frame outputs.
    /// Identical pose configurations share one skinned stream (skinned
    /// instancing): the stream contents depend only on the pose key, and the
    /// per-instance model matrix differentiates instances at draw time.</summary>
    public void PrepareFrame(long frameNumber, IEntityStore world)
    {
        BeginFrame(frameNumber);
        var groups = new Dictionary<SkinStreamKey, SkinGroup>();

        var entities = new List<ulong>(world.Entities);
        entities.Sort();
        foreach (ulong entityId in entities)
        {
            if (!world.TryGet(entityId, out AnimatorComponent animator) ||
                (animator.Flags & AnimatorComponent.ActiveFlag) == 0 ||
                AnimationAssetRegistry.GetSkeleton(animator.SkeletonId) == null ||
                AnimationAssetRegistry.GetClip(animator.BaseClipId) == null)
            {
                continue;
            }

            if (!world.TryGet(entityId, out ModelComponent modelComponent))
                continue;
            Model? model = AssetRegistry.GetModel(modelComponent.ModelId);
            if (model?.Parts == null)
                continue;

            var seenMeshes = new HashSet<ulong>();
            foreach (ModelPart part in model.Parts)
            {
                Mesh? mesh = AssetRegistry.GetMesh(part.MeshId);
                if (mesh == null ||
                    mesh.DeformationKind != MeshDeformationKind.Deforming ||
                    mesh.SkinSourceBuffer == null ||
                    !seenMeshes.Add(part.MeshId))
                {
                    continue;
                }

                var key = new SkinStreamKey(
                    part.MeshId,
                    animator.SkeletonId,
                    animator.BaseClipId,
                    animator.PlaybackRate,
                    animator.Time,
                    animator.Flags,
                    part.SkinnedOutputOffset);
                if (!groups.TryGetValue(key, out SkinGroup? group))
                {
                    group = new SkinGroup
                    {
                        Mesh = mesh,
                        VertexCount = mesh.VertexCount,
                        OutputOffset = part.SkinnedOutputOffset,
                    };
                    groups.Add(key, group);
                }
                group.Members.Add(entityId);
            }
        }

        if (groups.Count == 0)
            return;

        ulong totalBytes = 0;
        foreach (SkinGroup group in groups.Values)
        {
            totalBytes = checked(totalBytes +
                Align(checked((ulong)group.VertexCount * 48ul), 256ul));
        }
        EnsureBuffer(checked(Align(totalBytes, 256ul)));
        foreach (var pair in groups)
        {
            SkinStreamKey key = pair.Key;
            SkinGroup group = pair.Value;
            (RhiBuffer buffer, ulong address) = AllocateVertexStream(group.VertexCount);
            foreach (ulong member in group.Members)
            {
                _buffers[(member, key.MeshId)] = buffer;
                _addresses[(member, key.MeshId)] = address;
            }
            _uniqueBuffers.Add(buffer);
            _workItems.Add(new SkinWorkItem
            {
                EntityId = group.Members[0],
                MeshId = key.MeshId,
                Mesh = group.Mesh,
                VertexCount = group.VertexCount,
                OutputBuffer = buffer,
                OutputAddress = address,
                OutputOffset = group.OutputOffset,
            });
        }
    }

    /// <summary>Gets the frame number represented by the published addresses.</summary>
    public long FrameNumber { get; private set; } = -1;

    /// <summary>Gets the buffers containing current-frame dynamic vertices.</summary>
    public IReadOnlyCollection<RhiBuffer> DynamicVertexBuffers => _uniqueBuffers;

    /// <summary>Gets the compute skinning work items for this frame.</summary>
    public IReadOnlyList<SkinWorkItem> WorkItems => _workItems;

    /// <summary>Begins a frame and discards addresses from the previous frame.</summary>
    private void BeginFrame(long frameNumber)
    {
        FrameNumber = frameNumber;
        _bufferIndex = (int)(frameNumber % FrameBufferCount);
        _cursor = 0;
        _buffers.Clear();
        _addresses.Clear();
        _workItems.Clear();
        _uniqueBuffers.Clear();
    }

    /// <summary>Publishes the skin-matrix address used by one mesh work item.</summary>
    public void SetSkinMatrices(ulong entityId, ulong skinMatricesAddress)
    {
        foreach (SkinWorkItem workItem in _workItems)
        {
            if (workItem.EntityId == entityId)
                workItem.SkinMatricesAddress = skinMatricesAddress;
        }
    }

    /// <summary>Resolves a dynamic stream for one entity/mesh pair.</summary>
    public bool TryGet(
        ulong entityId,
        ulong meshId,
        out RhiBuffer? buffer,
        out ulong deviceAddress)
    {
        deviceAddress = 0;
        bool found = _buffers.TryGetValue((entityId, meshId), out buffer) &&
            _addresses.TryGetValue((entityId, meshId), out deviceAddress);
        return found;
    }

    private (RhiBuffer Buffer, ulong DeviceAddress) AllocateVertexStream(uint vertexCount)
    {
        ulong byteCount = checked((ulong)vertexCount * 48ul);
        ulong offset = Align(_cursor, 256ul);
        ulong required = checked(offset + Math.Max(byteCount, 48ul));
        RhiBuffer buffer = _dynamicBuffers[_bufferIndex]
            ?? throw new InvalidOperationException("Animation output buffer was not prepared.");
        if (required > buffer.Size)
            throw new InvalidOperationException("Animation output buffer was undersized before dispatch.");
        _cursor = required;
        return (buffer, checked(buffer.DeviceAddress + offset));
    }

    private void EnsureBuffer(ulong required)
    {
        required = Math.Max(required, 48ul);
        RhiBuffer? buffer = _dynamicBuffers[_bufferIndex];
        if (buffer != null && buffer.Size >= required)
            return;

        ulong size = Math.Max(InitialBufferBytes, buffer?.Size ?? 0ul);
        while (size < required)
            size = checked(size * 2ul);
        if (buffer != null)
            _retiredBuffers.Add(buffer);
        buffer = RhiBuffer.Create(
            _device,
            size,
            RhiNative.BufferUsage.Storage | RhiNative.BufferUsage.Vertex);
        buffer.SetDebugName(
            $"Animated vertices frame {_bufferIndex}",
            "Animation");
        _dynamicBuffers[_bufferIndex] = buffer;
    }

    private static ulong Align(ulong value, ulong alignment)
        => checked((value + alignment - 1ul) / alignment * alignment);

    /// <summary>Releases all frame-local dynamic buffers.</summary>
    public void Dispose()
    {
        _buffers.Clear();
        _addresses.Clear();
        _workItems.Clear();
        _uniqueBuffers.Clear();
        for (int index = 0; index < _dynamicBuffers.Length; ++index)
        {
            _dynamicBuffers[index]?.Dispose();
            _dynamicBuffers[index] = null;
        }
        foreach (RhiBuffer buffer in _retiredBuffers)
            buffer.Dispose();
        _retiredBuffers.Clear();
    }
}
