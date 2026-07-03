// SPDX-License-Identifier: MIT
// Executor: binds the swapchain back-buffer for the frame, walks the compiled
// graph's passes in order, and submits the C command-list once.
//
// Passes are responsible for creating their own RHI resources (typically in
// Setup or lazily in Execute). The executor does NOT lazy-create buffers
// from graph declarations any more due to a conflict between the pass's
// own creation and the executor's first-time auto-create.

using System;
using Engine.CBindings;
using Engine.RHI;

namespace Engine.RenderGraph;

public sealed class RenderGraphExecutor : ICommandSink, IDisposable
{
    private readonly RhiDevice _device;
    private readonly CommandRecorder _rec;
    private readonly RenderGraphContext _ctx = new();

    private RhiHeap? _transientHeap;
    private ulong _currentHeapSize;

    public RenderGraphExecutor(RhiDevice device)
    {
        _device = device;
        _rec = new CommandRecorder(device);
    }

    public RenderGraphContext Context => _ctx;

    /// <summary>Addressable back-buffer of the swapchain. The vertex pass
    /// looks it up by handle in its Execute() call.</summary>
    public void BindSwapchain(RhiTexture backBuffer, ResourceHandle handle,
                              ResourceState accessState = ResourceState.RenderTarget)
    {
        _ctx.Textures[handle] = backBuffer;
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
    public void Execute(RenderPlan graph)
    {
        AllocateTransientResources(graph);

        for (int i = 0; i < graph.Passes.Length; ++i)
        {
            var pass = graph.Passes[i];
            var barriers = graph.BarriersPerPass[i];
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
                _rec.PipelineBarrier(nativeBarriers);
            }
            pass.Execute(this, _ctx);
        }
        _rec.Submit();

        // Release transient wrappers after submission
        ReleaseTransientResources(graph);
    }

    private void AllocateTransientResources(RenderPlan graph)
    {
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
        => _rec.BeginRenderPass(color, colorLoad, colorStore, depth, depthLoad, depthStore);

    public void BeginComputePass(string? name = null) => _rec.BeginComputePass(name);
    public void EndComputePass() => _rec.EndComputePass();

    public void EndPass() => _rec.EndPass();
    public void BindPipeline(RhiPipeline pipeline) => _rec.BindPipeline(pipeline);
    public void BindVertexBuffer(uint slot, RhiBuffer buf, ulong offset = 0)
        => _rec.BindVertexBuffer(slot, buf, offset);


    public void BindTexture(uint slot, RhiTexture tex)
        => _rec.BindTexture(slot, tex);

    public void BindTextureArray(uint slot, RhiTexture[] texs)
        => _rec.BindTextureArray(slot, texs);

    public void BindSampler(uint slot, RhiSampler samp)
        => _rec.BindSampler(slot, samp);

    public void PushConstants(uint slot, uint size, IntPtr data)
        => _rec.PushConstants(slot, size, data);

    public void UseBuffer(RhiBuffer buf, uint usage = 1)
        => _rec.UseBuffer(buf, usage);

    public void BindIndexBuffer(RhiBuffer buf, bool is32Bit = false, ulong offset = 0)
        => _rec.BindIndexBuffer(buf, is32Bit, offset);
    public void SetViewport(float x, float y, float w, float h,
                            float minDepth = 0, float maxDepth = 1)
        => _rec.SetViewport(x, y, w, h, minDepth, maxDepth);

    public void SetScissor(uint x, uint y, uint w, uint h)
        => _rec.SetScissor(x, y, w, h);

    public void Draw(uint vertexCount, uint instanceCount = 1,
                     uint firstVertex = 0, uint firstInstance = 0)
        => _rec.Draw(vertexCount, instanceCount, firstVertex, firstInstance);

    public void DrawIndirect(RhiBuffer indirectBuffer, ulong offset, uint drawCount, uint stride)
        => _rec.DrawIndirect(indirectBuffer, offset, drawCount, stride);

    public void DrawIndexed(uint indexCount, uint instanceCount = 1,
                            uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
        => _rec.DrawIndexed(indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);

    public void DrawIndexedIndirect(RhiBuffer indirectBuffer, ulong offset, uint drawCount, uint stride)
        => _rec.DrawIndexedIndirect(indirectBuffer, offset, drawCount, stride);

    public void Dispatch(uint groupsX, uint groupsY, uint groupsZ)
        => _rec.Dispatch(groupsX, groupsY, groupsZ);

    public void Dispose()
    {
        _rec.Dispose();
        _transientHeap?.Dispose();
    }
}
