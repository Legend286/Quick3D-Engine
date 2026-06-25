// SPDX-License-Identifier: MIT
// Executor: binds the swapchain back-buffer for the frame, walks the compiled
// graph's passes in order, and submits the C command-list once.
//
// Passes are responsible for creating their own RHI resources (typically in
// Setup or lazily in Execute). The executor does NOT lazy-create buffers
// from graph declarations any more due to a conflict between the pass's
// own creation and the executor's first-time auto-create.

using Engine.CBindings;
using Engine.RHI;

namespace Engine.RenderGraph;

public sealed class RenderGraphExecutor : ICommandSink
{
    private readonly RhiDevice _device;
    private readonly CommandRecorder _rec;
    private readonly RenderGraphContext _ctx = new();

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

    /// <summary>Run the compiled graph: barriers (no-ops on Metal) → passes,
    /// then submit.</summary>
    public void Execute(RenderPlan graph)
    {
        for (int i = 0; i < graph.Passes.Length; ++i)
        {
            var pass = graph.Passes[i];
            // The barrier list is currently empty by way of Metal's implicit
            // dependency tracking, but the API is preserved for Phase 4
            // Vulkan (see docs/renderer/render-graph.md - barrier inference).
            _ = graph.BarriersPerPass[i];
            pass.Execute(this, _ctx);
        }
        _rec.Submit();
    }

    // ---- ICommandSink ----

    public void BeginRenderPass(RhiTexture color,
                                RhiNative.LoadOp colorLoad,
                                RhiNative.StoreOp colorStore,
                                RhiTexture? depth = null)
        => _rec.BeginRenderPass(color, colorLoad, colorStore, depth);

    public void EndPass() => _rec.EndPass();
    public void BindPipeline(RhiPipeline pipeline) => _rec.BindPipeline(pipeline);
    public void BindVertexBuffer(uint slot, RhiBuffer buf, ulong offset = 0)
        => _rec.BindVertexBuffer(slot, buf, offset);
    public void SetViewport(float x, float y, float w, float h,
                            float minDepth = 0, float maxDepth = 1)
        => _rec.SetViewport(x, y, w, h, minDepth, maxDepth);
    public void Draw(uint vertexCount, uint instanceCount = 1,
                     uint firstVertex = 0, uint firstInstance = 0)
        => _rec.Draw(vertexCount, instanceCount, firstVertex, firstInstance);
}
