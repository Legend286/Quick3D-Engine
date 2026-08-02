// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;

namespace Engine.Renderer;

internal readonly record struct VisibilityPickResult(
    ulong RequestId,
    ulong EntityId,
    uint PartIndex,
    uint PrimitiveIndex);

internal sealed class VisibilityPicker : IDisposable
{
    private const int SlotCount = 4;
    private const ulong IdentifierOffset = 0;
    private const ulong DepthOffset = 256;
    private const uint CopyRowStride = 256;
    private const ulong ReadbackBufferSize = 512;

    private readonly record struct Request(
        ulong Id,
        uint X,
        uint Y,
        uint Width,
        uint Height);

    private readonly record struct LookupEntry(
        ulong EntityId,
        uint LocalPartIndex);

    private sealed class Slot
    {
        public required RhiBuffer Buffer;
        public ulong SignalValue;
        public Request Request;
        public LookupEntry[] Lookup = Array.Empty<LookupEntry>();
        public bool Pending;
    }

    private readonly Queue<Request> _requests = new();
    private readonly Queue<VisibilityPickResult> _results = new();
    private readonly Slot[] _slots = new Slot[SlotCount];
    private readonly RhiFence _fence;
    private ulong _nextRequestId;
    private ulong _nextSignalValue;

    public VisibilityPicker(RhiDevice device)
    {
        _fence = new RhiFence(device);
        for (int index = 0; index < _slots.Length; ++index)
        {
            RhiBuffer buffer = RhiBuffer.Create(
                device,
                ReadbackBufferSize,
                RhiNative.BufferUsage.Storage);
            buffer.SetDebugName(
                $"Visibility Pick Readback {index}",
                "Editor Picking");
            _slots[index] = new Slot { Buffer = buffer };
        }
    }

    public bool HasPendingWork =>
        _requests.Count > 0 ||
        Array.Exists(_slots, slot => slot.Pending);

    public ulong Enqueue(uint x, uint y, uint width, uint height)
    {
        ulong requestId = ++_nextRequestId;
        _requests.Enqueue(new Request(
            requestId,
            x,
            y,
            Math.Max(width, 1u),
            Math.Max(height, 1u)));
        return requestId;
    }

    public ulong EnqueueMiss()
    {
        ulong requestId = ++_nextRequestId;
        _results.Enqueue(new VisibilityPickResult(
            requestId,
            0,
            0,
            0));
        return requestId;
    }

    public bool TryDequeueResult(out VisibilityPickResult result)
    {
        PollCompleted();
        return _results.TryDequeue(out result);
    }

    public void Record(
        ICommandSink sink,
        RhiTexture identifiers,
        RhiTexture depth,
        RenderGraphContext context,
        SceneFrameData frameData)
    {
        if (_requests.Count == 0)
            return;

        Slot? slot = Array.Find(_slots, candidate => !candidate.Pending);
        if (slot == null)
            return;

        Request request = _requests.Dequeue();
        if (frameData.Parts.Count == 0)
        {
            _results.Enqueue(new VisibilityPickResult(
                request.Id,
                0,
                0,
                0));
            return;
        }

        uint width = Math.Max(context.Width, 1u);
        uint height = Math.Max(context.Height, 1u);
        uint sourceX = (uint)Math.Min(
            (ulong)request.X * width / request.Width,
            width - 1u);
        uint sourceY = (uint)Math.Min(
            (ulong)request.Y * height / request.Height,
            height - 1u);

        slot.Request = request;
        slot.Lookup = BuildLookup(frameData);
        slot.SignalValue = ++_nextSignalValue;
        slot.Pending = true;

        sink.CopyTextureToBuffer(
            identifiers,
            sourceX,
            sourceY,
            1,
            1,
            slot.Buffer,
            IdentifierOffset,
            CopyRowStride);
        sink.CopyTextureToBuffer(
            depth,
            sourceX,
            sourceY,
            1,
            1,
            slot.Buffer,
            DepthOffset,
            CopyRowStride);
        sink.SignalFence(_fence, slot.SignalValue);
    }

    public void ResetAfterGpuIdle()
    {
        _requests.Clear();
        _results.Clear();
        foreach (Slot slot in _slots)
        {
            slot.Pending = false;
            slot.Lookup = Array.Empty<LookupEntry>();
        }
    }

    private void PollCompleted()
    {
        ulong completedValue = _fence.CompletedValue;
        foreach (Slot slot in _slots)
        {
            if (!slot.Pending || slot.SignalValue > completedValue)
                continue;

            VisibilityIdentifiers identifiers =
                slot.Buffer.ReadMapped<VisibilityIdentifiers>(
                    IdentifierOffset);
            float depth = slot.Buffer.ReadMapped<float>(DepthOffset);
            LookupEntry entry =
                depth < 0.999999f &&
                identifiers.PartIndex < (uint)slot.Lookup.Length
                    ? slot.Lookup[(int)identifiers.PartIndex]
                    : default;
            _results.Enqueue(new VisibilityPickResult(
                slot.Request.Id,
                entry.EntityId,
                entry.LocalPartIndex,
                identifiers.PrimitiveIndex));
            slot.Pending = false;
            slot.Lookup = Array.Empty<LookupEntry>();
        }
    }

    private static LookupEntry[] BuildLookup(SceneFrameData frameData)
    {
        var result = new LookupEntry[frameData.Parts.Count];
        for (int partIndex = 0;
             partIndex < frameData.Parts.Count;
             ++partIndex)
        {
            PartData part = frameData.Parts[partIndex];
            if (part.InstanceIdx >= (uint)frameData.Instances.Count)
                continue;
            InstanceData instance =
                frameData.Instances[(int)part.InstanceIdx];
            uint localPartIndex =
                partIndex >= instance.FirstPartIndex
                    ? (uint)partIndex - instance.FirstPartIndex
                    : 0;
            result[partIndex] = new LookupEntry(
                instance.EntityIdLow |
                    ((ulong)instance.EntityIdHigh << 32),
                localPartIndex);
        }
        return result;
    }

    public void Dispose()
    {
        foreach (Slot slot in _slots)
            slot.Buffer.Dispose();
        _fence.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct VisibilityIdentifiers(
        uint PartIndex,
        uint PrimitiveIndex);
}

internal sealed class VisibilityPickingPass : RenderPass
{
    private readonly VisibilityPicker _picker;
    private readonly RasterSceneGpuCache _sceneCache;

    public VisibilityPickingPass(
        VisibilityPicker picker,
        RasterSceneGpuCache sceneCache)
    {
        _picker = picker;
        _sceneCache = sceneCache;
        Name = "Visibility Picking Readback";
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.Read(
            RenderGraphResources.VisibilityIdentifiersHandle,
            ResourceState.CopySrc);
        builder.Read(
            RenderGraphResources.DepthBufferHandle,
            ResourceState.CopySrc);
    }

    public override void Execute(
        ICommandSink sink,
        RenderGraphContext context)
    {
        if (!context.TryGetTexture(
                RenderGraphResources.VisibilityIdentifiersHandle,
                out RhiTexture identifiers) ||
            !context.TryGetTexture(
                RenderGraphResources.DepthBufferHandle,
                out RhiTexture depth))
        {
            return;
        }

        _picker.Record(
            sink,
            identifiers,
            depth,
            context,
            _sceneCache.FrameData);
    }
}
