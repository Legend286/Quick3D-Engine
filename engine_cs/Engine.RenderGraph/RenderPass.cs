// SPDX-License-Identifier: MIT
// Render pass base class. Subclasses declare resources in Setup() and record
// commands in Execute().

using Engine.RHI;

namespace Engine.RenderGraph;

public abstract class RenderPass
{
    public string Name { get; init; } = string.Empty;

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
                                RhiTexture? depth = null);

    public void EndPass();
    public void BindPipeline(RhiPipeline pipeline);
    public void BindVertexBuffer(uint slot, RhiBuffer buf, ulong offset = 0);
    public void SetViewport(float x, float y, float w, float h,
                            float minDepth = 0, float maxDepth = 1);
    public void Draw(uint vertexCount, uint instanceCount = 1,
                     uint firstVertex = 0, uint firstInstance = 0);
}

public sealed class RenderGraphContext
{
    // Resolved real RHI handles for the resources accesses by this frame's
    // passes. Filled in by RenderGraphExecutor before Execute runs.
    public System.Collections.Generic.Dictionary<ResourceHandle, RhiTexture> Textures { get; } = new();
    public System.Collections.Generic.Dictionary<ResourceHandle, RhiBuffer> Buffers { get; } = new();

    public bool TryGetTexture(ResourceHandle h, out RhiTexture t) => Textures.TryGetValue(h, out t!);
    public bool TryGetBuffer(ResourceHandle h, out RhiBuffer b) => Buffers.TryGetValue(h, out b!);
}
