// SPDX-License-Identifier: MIT
// Render pass base class. Subclasses declare resources in Setup() and record
// commands in Execute().

using Engine.CBindings;
using Engine.RHI;

namespace Engine.RenderGraph;

public abstract class RenderPass
{
    public string Name { get; init; } = string.Empty;
    public RhiNative.QueueType Queue { get; init; } = RhiNative.QueueType.Graphics;

    /// <summary>Declare the pass's resource reads/writes.</summary>
    public abstract void Setup(RenderGraphBuilder builder);

    /// <summary>Record GPU commands. Real RHI handles are obtained from the
    /// context, NOT from the builder (which is only valid during compile).</summary>
    public abstract void Execute(ICommandSink sink, RenderGraphContext context);

    /// <summary>Per-pass execution state that survives across its Execute call.
    /// Used by the executor to look up the bound resource handles.</summary>
}

public interface ICommandSink
{
    /// <summary>Begin a render pass on the given color/depth RHI handles.</summary>
    public void BeginRenderPass(RhiTexture color,
                                RhiNative.LoadOp colorLoad,
                                RhiNative.StoreOp colorStore,
                                RhiTexture? depth = null,
                                RhiNative.LoadOp depthLoad = RhiNative.LoadOp.Clear,
                                RhiNative.StoreOp depthStore = RhiNative.StoreOp.Store);
 
    public void BeginComputePass(string? name = null);
    public void EndComputePass();

    public void EndPass();
    public void BindPipeline(RhiPipeline pipeline);
    public void BindVertexBuffer(uint slot, RhiBuffer buf, ulong offset = 0);
    public void PushConstants(uint slot, uint size, IntPtr data);
    public void SetViewport(float x, float y, float w, float h,
                            float minDepth = 0, float maxDepth = 1);
    public void SetScissor(uint x, uint y, uint w, uint h);
    public void BindTexture(uint slot, RhiTexture tex);
    public void BindTextureArray(uint slot, RhiTexture[] texs);
    public void BindHeap(uint slot, RhiBindlessHeap heap);
    public void BindSampler(uint slot, RhiSampler samp);
    public void UseBuffer(RhiBuffer buf, uint usage = 1);
    public void BindIndexBuffer(RhiBuffer buf, bool is32Bit = false, ulong offset = 0);
    public void Draw(uint vertexCount, uint instanceCount = 1,
                     uint firstVertex = 0, uint firstInstance = 0);
    public void DrawIndirect(RhiBuffer indirectBuffer, ulong offset, uint drawCount, uint stride);
    public void DrawIndexed(uint indexCount, uint instanceCount = 1,
                            uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0);
    public void DrawIndexedIndirect(RhiBuffer indirectBuffer, ulong offset, uint drawCount, uint stride);
    public void Dispatch(uint groupsX, uint groupsY, uint groupsZ);
}

public sealed class RenderGraphContext
{
    // Resolved real RHI handles for the resources accesses by this frame's
    // passes. Filled in by RenderGraphExecutor before Execute runs.
    public System.Collections.Generic.Dictionary<ResourceHandle, RhiTexture> Textures { get; } = new();
    public System.Collections.Generic.Dictionary<ResourceHandle, RhiBuffer> Buffers { get; } = new();

    // Logical frame dimensions (swapchain size in physical pixels). Set by
    // RenderGraphExecutor.SetViewportSize before Execute runs so passes can
    // pick the correct render-target viewport without re-reading the swapchain
    // hand-bound by the Renderer.
    public uint Width;
    public uint Height;

    public bool TryGetTexture(ResourceHandle h, out RhiTexture t) => Textures.TryGetValue(h, out t!);
    public bool TryGetBuffer(ResourceHandle h, out RhiBuffer b) => Buffers.TryGetValue(h, out b!);
}
