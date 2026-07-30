// SPDX-License-Identifier: MIT
// Executor: binds the swapchain back-buffer for the frame, walks the compiled
// graph's passes in order, and submits the C command-list once.
//
// Passes are responsible for creating their own RHI resources (typically in
// Setup or lazily in Execute). The executor does NOT lazy-create buffers
// from graph declarations any more due to a conflict between the pass's
// own creation and the executor's first-time auto-create.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Engine.CBindings;
using Engine.RHI;

namespace Engine.RenderGraph;

public sealed class RenderGraphExecutor : ICommandSink, IDisposable
{
    private const int GpuFrameHistoryCapacity = 15;

    private readonly RhiDevice _device;
    private CommandRecorder? _rec;
    private readonly RenderGraphContext _ctx = new();
    private readonly RhiFence _asyncComputeFence;
    private readonly RhiFence _graphicsCompletionFence;
    private ulong _asyncComputeFenceValue;
    private ulong _graphicsCompletionFenceValue;
    private RenderPassTiming[] _lastPassTimings = Array.Empty<RenderPassTiming>();
    private readonly RhiTimestampQueryPool?[] _timestampPools = new RhiTimestampQueryPool?[3];
    private readonly long[] _timestampPoolFrames = new long[3];
    private readonly RhiTimestampQueryPool?[] _computeTimestampPools = new RhiTimestampQueryPool?[3];
    private readonly long[] _computeTimestampPoolFrames = new long[3];
    private double?[] _lastGpuPassMilliseconds = Array.Empty<double?>();
    private readonly double[] _gpuFrameHistory =
        new double[GpuFrameHistoryCapacity];
    private int _gpuFrameHistoryCount;
    private int _gpuFrameHistoryIndex;
    private double? _lastRawGpuFrameMilliseconds;
    private double? _lastGpuFrameMilliseconds;
    private long _lastGpuTimingFrameNumber = -1;
    private int _timestampPassCount;
    private int _nextTimestampPool;
    private int _nextComputeTimestampPool;
    private long _executionNumber;

    private RhiHeap? _transientHeap;
    private ulong _currentHeapSize;

    public RenderGraphExecutor(RhiDevice device)
    {
        _device = device;
        _asyncComputeFence = new RhiFence(device);
        _graphicsCompletionFence = new RhiFence(device);
    }

    public RenderGraphContext Context => _ctx;
    public IReadOnlyList<RenderPassTiming> LastPassTimings => _lastPassTimings;
    /// <summary>
    /// Gets the command-buffer span for same-capture sample validation.
    /// </summary>
    public double? LastRawGpuFrameMilliseconds =>
        _lastRawGpuFrameMilliseconds;
    public double? LastGpuFrameMilliseconds => _lastGpuFrameMilliseconds;
    public long LastGpuTimingFrameNumber => _lastGpuTimingFrameNumber;
    public bool EnableGpuTiming { get; set; }
    private CommandRecorder Recorder => _rec ?? throw new InvalidOperationException("No render graph execution is active.");

    /// <summary>Addressable back-buffer of the swapchain. The vertex pass
    /// looks it up by handle in its Execute() call.</summary>
    public void BindSwapchain(RhiTexture backBuffer, ResourceHandle handle,
                              ResourceState accessState = ResourceState.RenderTarget)
    {
        _ctx.Textures[handle] = backBuffer;
    }

    /// <summary>Removes an imported texture that is no longer in the plan.</summary>
    public void UnbindTexture(ResourceHandle handle)
    {
        _ctx.Textures.Remove(handle);
    }

    /// <summary>Publish the logical frame dimensions to the context so
    /// passes can size their viewport/scissor without re-reading the
    /// swapchain image (which carries no public width/height).</summary>
    public void SetViewportSize(uint width, uint height)
    {
        _ctx.Width  = width  > 0 ? width  : 1;
        _ctx.Height = height > 0 ? height : 1;
    }

    /// <summary>Run the compiled graph: setup transient memory → barriers → passes,
    /// then submit.</summary>
    public void Execute(RenderPlan graph, RhiFence? waitFence = null, ulong waitValue = 0, RhiFence? signalFence = null, ulong signalValue = 0)
    {
        _ctx.FrameNumber = _executionNumber;
        PollGpuTimings(graph.Passes.Length);
        RhiTimestampQueryPool? timestampPool = AcquireTimestampPool(graph.Passes.Length);
        RhiTimestampQueryPool? computeTimestampPool =
            CanUseAsyncCompute(graph)
                ? AcquireTimestampPool(graph.Passes.Length, compute: true)
                : null;
        bool recordingGpuTimestamps = timestampPool != null;
        bool recordingComputeGpuTimestamps = computeTimestampPool != null;
        bool useAsyncCompute = CanUseAsyncCompute(graph);
        using var graphicsRecorder = new CommandRecorder(_device);
        using var computeRecorder = useAsyncCompute
            ? new CommandRecorder(_device, RhiNative.QueueType.Compute)
            : null;
        ulong asyncFenceValue = useAsyncCompute
            ? ++_asyncComputeFenceValue
            : 0;
        ulong previousGraphicsCompletionValue =
            _graphicsCompletionFenceValue;
        ulong graphicsCompletionValue = useAsyncCompute
            ? ++_graphicsCompletionFenceValue
            : 0;
        HashSet<ResourceHandle> computeWrites = useAsyncCompute
            ? GetComputeWrites(graph)
            : new HashSet<ResourceHandle>();
        bool graphicsWaitEncoded = false;
        var timings = new RenderPassTiming[graph.Passes.Length];

        try
        {
            AllocateTransientResources(graph);

            if (waitFence != null && waitValue > 0)
            {
                graphicsRecorder.WaitFence(waitFence, waitValue);
                computeRecorder?.WaitFence(waitFence, waitValue);
            }
            if (useAsyncCompute && previousGraphicsCompletionValue > 0)
            {
                computeRecorder!.WaitFence(
                    _graphicsCompletionFence,
                    previousGraphicsCompletionValue);
            }

            for (int i = 0; i < graph.Passes.Length; ++i)
            {
                var pass = graph.Passes[i];
                var barriers = graph.BarriersPerPass[i];
                bool onAsyncCompute =
                    useAsyncCompute &&
                    pass.Queue == RhiNative.QueueType.Compute;
                CommandRecorder recorder = onAsyncCompute
                    ? computeRecorder!
                    : graphicsRecorder;
                _rec = recorder;
                if (!onAsyncCompute &&
                    useAsyncCompute &&
                    !graphicsWaitEncoded &&
                    graph.PassAccesses[i].Any(access =>
                        computeWrites.Contains(access.Resource)))
                {
                    recorder.WaitFence(_asyncComputeFence, asyncFenceValue);
                    graphicsWaitEncoded = true;
                }

                long startTimestamp = Stopwatch.GetTimestamp();
                bool capturePassGpuTiming =
                    onAsyncCompute
                        ? recordingComputeGpuTimestamps
                        : recordingGpuTimestamps;
                if (capturePassGpuTiming)
                {
                    bool recorded = recorder.BeginTimestampScope(
                        onAsyncCompute ? computeTimestampPool! : timestampPool!,
                        (uint)(i * 2));
                    if (onAsyncCompute)
                        recordingComputeGpuTimestamps = recorded;
                    else
                        recordingGpuTimestamps = recorded;
                }
                if (barriers.Count > 0)
                {
                    var nativeBarriers = new Engine.CBindings.RhiNative.Barrier[barriers.Count];
                    for (int b = 0; b < barriers.Count; b++)
                    {
                        nativeBarriers[b] = new Engine.CBindings.RhiNative.Barrier
                        {
                            Resource = barriers[b].Resource.Id,
                            StateBefore = (Engine.CBindings.RhiNative.ResourceState)barriers[b].StateBefore,
                            StateAfter = (Engine.CBindings.RhiNative.ResourceState)barriers[b].StateAfter,
                        };
                    }
                    recorder.PipelineBarrier(nativeBarriers);
                }
                pass.Execute(this, _ctx);
                if (capturePassGpuTiming &&
                    (onAsyncCompute
                        ? recordingComputeGpuTimestamps
                        : recordingGpuTimestamps))
                {
                    bool recorded = recorder.EndTimestampScope(
                        onAsyncCompute ? computeTimestampPool! : timestampPool!,
                        (uint)(i * 2 + 1));
                    if (onAsyncCompute)
                        recordingComputeGpuTimestamps = recorded;
                    else
                        recordingGpuTimestamps = recorded;
                }
                timings[i] = new RenderPassTiming(
                    pass.Name,
                    Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                    i < _lastGpuPassMilliseconds.Length
                        ? _lastGpuPassMilliseconds[i]
                        : null);
            }

            if (useAsyncCompute)
                computeRecorder!.SignalFence(_asyncComputeFence, asyncFenceValue);
            if (useAsyncCompute &&
                recordingComputeGpuTimestamps &&
                computeRecorder!.ResolveTimestamps(
                    computeTimestampPool!,
                    (uint)(graph.Passes.Length * 2)))
            {
                int submittedPool = Array.IndexOf(
                    _computeTimestampPools,
                    computeTimestampPool);
                if (submittedPool >= 0)
                    _computeTimestampPoolFrames[submittedPool] = _executionNumber;
            }
            if (recordingGpuTimestamps &&
                graphicsRecorder.ResolveTimestamps(
                    timestampPool!,
                    (uint)(graph.Passes.Length * 2)))
            {
                int submittedPool = Array.IndexOf(_timestampPools, timestampPool);
                if (submittedPool >= 0)
                    _timestampPoolFrames[submittedPool] = _executionNumber;
            }
            if (signalFence != null && signalValue > 0)
                graphicsRecorder.SignalFence(signalFence, signalValue);
            if (useAsyncCompute)
            {
                graphicsRecorder.SignalFence(
                    _graphicsCompletionFence,
                    graphicsCompletionValue);
            }
            computeRecorder?.Submit();
            graphicsRecorder.Submit();

            _lastPassTimings = timings;
            _executionNumber++;
            ReleaseTransientResources(graph);
        }
        finally
        {
            _rec = null;
        }
    }

    private static bool CanUseAsyncCompute(RenderPlan graph)
    {
        bool foundCompute = false;
        bool foundGraphics = false;
        foreach (RenderPass pass in graph.Passes)
        {
            if (pass.Queue == RhiNative.QueueType.Compute)
            {
                if (foundGraphics)
                    return false;
                foundCompute = true;
            }
            else
            {
                foundGraphics = true;
            }
        }
        return foundCompute;
    }

    private static HashSet<ResourceHandle> GetComputeWrites(RenderPlan graph)
    {
        var writes = new HashSet<ResourceHandle>();
        for (int i = 0; i < graph.Passes.Length; ++i)
        {
            if (graph.Passes[i].Queue != RhiNative.QueueType.Compute)
                continue;
            foreach (AccessDecl access in graph.PassAccesses[i])
            {
                if (access.Access is ResourceAccess.Write or ResourceAccess.ReadWrite)
                    writes.Add(access.Resource);
            }
        }
        return writes;
    }

    private RhiTimestampQueryPool? AcquireTimestampPool(int passCount)
        => AcquireTimestampPool(passCount, compute: false);

    private RhiTimestampQueryPool? AcquireTimestampPool(
        int passCount,
        bool compute)
    {
        if (!EnableGpuTiming || passCount == 0)
            return null;

        EnsureTimestampPools(passCount);
        RhiTimestampQueryPool?[] pools =
            compute ? _computeTimestampPools : _timestampPools;
        int nextPool = compute ? _nextComputeTimestampPool : _nextTimestampPool;
        for (int attempt = 0; attempt < pools.Length; ++attempt)
        {
            int index = (nextPool + attempt) % pools.Length;
            RhiTimestampQueryPool? pool = pools[index];
            if (pool is not { HasPendingResults: false })
                continue;

            if (compute)
                _nextComputeTimestampPool = (index + 1) % pools.Length;
            else
                _nextTimestampPool = (index + 1) % pools.Length;
            return pool;
        }
        return null;
    }

    private void EnsureTimestampPools(int passCount)
    {
        if (_timestampPassCount == passCount)
            return;

        DisposeTimestampPools();
        _timestampPassCount = passCount;
        _lastGpuPassMilliseconds = new double?[passCount];
        uint sampleCount = checked((uint)(passCount * 2));
        for (int i = 0; i < _timestampPools.Length; ++i)
        {
            _timestampPools[i] = RhiTimestampQueryPool.TryCreate(_device, sampleCount);
            _computeTimestampPools[i] =
                RhiTimestampQueryPool.TryCreate(_device, sampleCount);
        }
    }

    private void PollGpuTimings(int passCount)
    {
        if (!EnableGpuTiming || _timestampPassCount != passCount)
            return;

        long newestFrame = -1;
        double? newestFrameMilliseconds = null;
        double?[]? newestDurations = null;
        for (int poolIndex = 0; poolIndex < _timestampPools.Length; ++poolIndex)
        {
            RhiTimestampQueryPool? pool = _timestampPools[poolIndex];
            if (pool is not { HasPendingResults: true })
                continue;

            double? poolFrameMilliseconds =
                pool.TryReadFrameDuration(
                    out ulong frameDurationNanoseconds)
                    ? frameDurationNanoseconds / 1_000_000.0
                    : null;

            var durationsNanoseconds = new ulong[passCount];
            if (!pool.TryReadDurations(durationsNanoseconds) ||
                _timestampPoolFrames[poolIndex] <= newestFrame)
            {
                continue;
            }

            newestFrame = _timestampPoolFrames[poolIndex];
            newestFrameMilliseconds = poolFrameMilliseconds;
            newestDurations = new double?[passCount];
            for (int passIndex = 0; passIndex < passCount; ++passIndex)
            {
                ulong duration = durationsNanoseconds[passIndex];
                newestDurations[passIndex] = duration == ulong.MaxValue
                    ? null
                    : duration / 1_000_000.0;
            }
        }

        PollComputeGpuTimings(passCount);

        if (newestDurations != null)
        {
            double activeGraphicsMilliseconds = 0.0;
            bool hasActiveGraphicsDuration = false;
            for (int passIndex = 0;
                 passIndex < newestDurations.Length;
                 ++passIndex)
            {
                _lastGpuPassMilliseconds[passIndex] =
                    newestDurations[passIndex];
                if (newestDurations[passIndex] is double duration &&
                    double.IsFinite(duration) &&
                    duration >= 0.0)
                {
                    activeGraphicsMilliseconds += duration;
                    hasActiveGraphicsDuration = true;
                }
            }
            _lastGpuTimingFrameNumber = newestFrame;
            _lastRawGpuFrameMilliseconds =
                newestFrameMilliseconds;
            if (hasActiveGraphicsDuration &&
                activeGraphicsMilliseconds > 0.0)
            {
                _lastGpuFrameMilliseconds =
                    RecordGpuFrameDuration(
                        activeGraphicsMilliseconds);
            }
        }
    }

    private double RecordGpuFrameDuration(double milliseconds)
    {
        _gpuFrameHistory[_gpuFrameHistoryIndex] = milliseconds;
        _gpuFrameHistoryIndex =
            (_gpuFrameHistoryIndex + 1) % GpuFrameHistoryCapacity;
        _gpuFrameHistoryCount = Math.Min(
            _gpuFrameHistoryCount + 1,
            GpuFrameHistoryCapacity);

        Span<double> sorted = stackalloc double[GpuFrameHistoryCapacity];
        for (int i = 0; i < _gpuFrameHistoryCount; ++i)
            sorted[i] = _gpuFrameHistory[i];
        for (int i = 1; i < _gpuFrameHistoryCount; ++i)
        {
            double value = sorted[i];
            int insertionIndex = i - 1;
            while (insertionIndex >= 0 &&
                   sorted[insertionIndex] > value)
            {
                sorted[insertionIndex + 1] =
                    sorted[insertionIndex];
                --insertionIndex;
            }
            sorted[insertionIndex + 1] = value;
        }

        return sorted[(_gpuFrameHistoryCount - 1) / 2];
    }

    private void PollComputeGpuTimings(int passCount)
    {
        long newestFrame = -1;
        double?[]? newestDurations = null;
        for (int poolIndex = 0;
             poolIndex < _computeTimestampPools.Length;
             ++poolIndex)
        {
            RhiTimestampQueryPool? pool = _computeTimestampPools[poolIndex];
            if (pool is not { HasPendingResults: true })
                continue;

            var durationsNanoseconds = new ulong[passCount];
            if (!pool.TryReadDurations(durationsNanoseconds) ||
                _computeTimestampPoolFrames[poolIndex] <= newestFrame)
            {
                continue;
            }

            newestFrame = _computeTimestampPoolFrames[poolIndex];
            newestDurations = new double?[passCount];
            for (int passIndex = 0; passIndex < passCount; ++passIndex)
            {
                ulong duration = durationsNanoseconds[passIndex];
                newestDurations[passIndex] = duration == ulong.MaxValue
                    ? null
                    : duration / 1_000_000.0;
            }
        }

        if (newestDurations == null)
            return;
        for (int passIndex = 0; passIndex < newestDurations.Length; ++passIndex)
        {
            if (newestDurations[passIndex] is double duration)
                _lastGpuPassMilliseconds[passIndex] = duration;
        }
    }

    private void DisposeTimestampPools()
    {
        foreach (RhiTimestampQueryPool? pool in _timestampPools)
            pool?.Dispose();
        foreach (RhiTimestampQueryPool? pool in _computeTimestampPools)
            pool?.Dispose();
        Array.Clear(_timestampPools);
        Array.Clear(_timestampPoolFrames);
        Array.Clear(_computeTimestampPools);
        Array.Clear(_computeTimestampPoolFrames);
        _timestampPassCount = 0;
        Array.Clear(_gpuFrameHistory);
        _gpuFrameHistoryCount = 0;
        _gpuFrameHistoryIndex = 0;
        _lastRawGpuFrameMilliseconds = null;
        _lastGpuFrameMilliseconds = null;
        _lastGpuTimingFrameNumber = -1;
    }

    private void AllocateTransientResources(RenderPlan graph)
    {
        if (graph.Aliasing.TotalHeapSize <= 0)
            return;

        if (graph.Aliasing.TotalHeapSize > _currentHeapSize || _transientHeap == null)
        {
            _transientHeap?.Dispose();
            _currentHeapSize = (ulong)(graph.Aliasing.TotalHeapSize * 1.2);
            // Ensure minimum 1MB and align to 64KB (Metal heap requirements)
            if (_currentHeapSize < 1024 * 1024) _currentHeapSize = 1024 * 1024;
            _currentHeapSize = (_currentHeapSize + 65535) & ~65535ul;

            if (_currentHeapSize > 0)
            {
                _transientHeap = new RhiHeap(_device, _currentHeapSize, RhiNative.HeapUsageRenderTarget | RhiNative.HeapUsageShaderRead);
            }
        }

        if (_transientHeap == null) return;

        foreach (var (handle, decl) in graph.ResourceDecls)
        {
            if (!graph.Aliasing.ResourceOffsets.TryGetValue(handle, out ulong offset)) continue;

            if (decl.Kind == ResourceKind.Texture)
            {
                var texDesc = new RhiNative.TextureDesc
                {
                    Abi = 1,
                    Width = decl.Texture!.Width,
                    Height = decl.Texture!.Height,
                    MipLevels = decl.Texture!.MipLevels,
                    Format = decl.Texture!.Format,
                    UsageFlags = decl.Texture!.UsageFlags
                };
                _ctx.Textures[handle] = _transientHeap.CreateTexture(_device, texDesc, offset);
            }
            else if (decl.Kind == ResourceKind.Buffer)
            {
                var bufDesc = new RhiNative.BufferDesc
                {
                    Abi = 1,
                    Size = decl.Buffer!.Size,
                    Usage = decl.Buffer!.Usage
                };
                _ctx.Buffers[handle] = _transientHeap.CreateBuffer(_device, bufDesc, offset);
            }
        }
    }

    private void ReleaseTransientResources(RenderPlan graph)
    {
        // We dispose the transient wrappers. The underlying memory stays in the heap.
        foreach (var handle in graph.ResourceDecls.Keys)
        {
            if (_ctx.Textures.TryGetValue(handle, out var tex))
            {
                tex.Dispose();
                _ctx.Textures.Remove(handle);
            }
            if (_ctx.Buffers.TryGetValue(handle, out var buf))
            {
                buf.Dispose();
                _ctx.Buffers.Remove(handle);
            }
        }
    }

    // ---- ICommandSink ----

    public void BeginRenderPass(RhiTexture color,
                                RhiNative.LoadOp colorLoad,
                                RhiNative.StoreOp colorStore,
                                RhiTexture? depth = null,
                                RhiNative.LoadOp depthLoad = RhiNative.LoadOp.Clear,
                                RhiNative.StoreOp depthStore = RhiNative.StoreOp.Store)
        => Recorder.BeginRenderPass(color, colorLoad, colorStore, depth, depthLoad, depthStore);

    public void BeginDepthOnlyPass(
        RhiTexture depth,
        RhiNative.LoadOp depthLoad = RhiNative.LoadOp.Clear,
        RhiNative.StoreOp depthStore = RhiNative.StoreOp.Store)
        => Recorder.BeginDepthOnlyPass(depth, depthLoad, depthStore);

    public void BeginComputePass(string? name = null) => Recorder.BeginComputePass(name);
    public void EndComputePass() => Recorder.EndComputePass();

    public void EndPass() => Recorder.EndPass();
    public void Submit() => Recorder.Submit();
    public void SubmitAndWait() => Recorder.SubmitAndWait();
    public void BindPipeline(RhiPipeline pipeline) => Recorder.BindPipeline(pipeline);
    public void BindVertexBuffer(uint slot, RhiBuffer buf, ulong offset = 0)
        => Recorder.BindVertexBuffer(slot, buf, offset);


    public void BindTexture(uint slot, RhiTexture tex)
        => Recorder.BindTexture(slot, tex);

    public void BindTextureArray(uint slot, RhiTexture[] texs)
        => Recorder.BindTextureArray(slot, texs);

    public void BindHeap(uint slot, RhiBindlessHeap heap)
        => Recorder.BindHeap(slot, heap);

    public void BindSampler(uint slot, RhiSampler samp)
        => Recorder.BindSampler(slot, samp);

    public void PushConstants(uint slot, uint size, IntPtr data)
        => Recorder.PushConstants(slot, size, data);

    public void UseBuffer(RhiBuffer buf, uint usage = 1)
        => Recorder.UseBuffer(buf, usage);

    public void BindIndexBuffer(RhiBuffer buf, bool is32Bit = false, ulong offset = 0)
        => Recorder.BindIndexBuffer(buf, is32Bit, offset);
    public void SetViewport(float x, float y, float w, float h,
                            float minDepth = 0, float maxDepth = 1)
        => Recorder.SetViewport(x, y, w, h, minDepth, maxDepth);

    public void SetScissor(uint x, uint y, uint w, uint h)
        => Recorder.SetScissor(x, y, w, h);

    public void Draw(uint vertexCount, uint instanceCount = 1,
                     uint firstVertex = 0, uint firstInstance = 0)
        => Recorder.Draw(vertexCount, instanceCount, firstVertex, firstInstance);

    public void DrawIndirect(RhiBuffer indirectBuffer, ulong offset, uint drawCount, uint stride)
        => Recorder.DrawIndirect(indirectBuffer, offset, drawCount, stride);

    public void DrawIndexed(uint indexCount, uint instanceCount = 1,
                            uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
        => Recorder.DrawIndexed(indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);

    public void DrawIndexedIndirect(RhiBuffer indirectBuffer, ulong offset, uint drawCount, uint stride)
        => Recorder.DrawIndexedIndirect(indirectBuffer, offset, drawCount, stride);

    public void Dispatch(uint groupsX, uint groupsY, uint groupsZ,
                          uint threadsX = 64, uint threadsY = 1, uint threadsZ = 1)
        => Recorder.Dispatch(groupsX, groupsY, groupsZ, threadsX, threadsY, threadsZ);

    public void Dispose()
    {
        DisposeTimestampPools();
        _asyncComputeFence.Dispose();
        _graphicsCompletionFence.Dispose();
        _transientHeap?.Dispose();
    }
    
    public void BindAccelStruct(uint slot, RhiAccelStruct as_handle) => Recorder.BindAccelStruct(slot, as_handle);
    public void UseAccelStruct(RhiAccelStruct as_handle, uint usage = 1) => Recorder.UseAccelStruct(as_handle, usage);
    public void BuildAccelStructs(ReadOnlySpan<RhiAccelStruct> accelStructs) => Recorder.BuildAccelStructs(accelStructs);
    public void CompactAccelStructs(ReadOnlySpan<RhiAccelStruct> accelStructs) => Recorder.CompactAccelStructs(accelStructs);
}

public readonly record struct RenderPassTiming(
    string Name,
    double CpuMilliseconds,
    double? GpuMilliseconds);
