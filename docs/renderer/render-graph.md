# Render Graph (Phase 2)

> Status: MVP1 (topological sort + forward state barrier inference). Async compute,
> parallel barriers, and transient memory aliasing are deferred to post-MVP1.

## Goals

The C# render graph is the frame orchestrator. It owns:

- **Resource lifecycle**: transient textures + buffers per frame.
- **Barrier inference**: insertion of state transitions between consecutive
  passes that touch the same resource with different states.
- **Schedule linearization**: pass order becomes the per-frame submission
  order. MVP1 uses registration order as the topological schedule.
- **Command buffer packaging**: a single C `RhiCommandList*` spans all
  passes' GPU work for one frame.

Deferred to post-MVP1: split barriers for parallel execution, async compute
queues, memory aliasing for transient resources.

## Surface (C#)

```csharp
public abstract class RenderPass {
    public string Name { get; init; }
    public abstract void Setup(RenderGraphBuilder b);
    public abstract void Execute(ICommandSink enc, RenderGraphContext ctx);
}

public sealed class RenderGraphBuilder {
    public ResourceHandle CreateTexture(TextureDesc d);
    public ResourceHandle CreateBuffer (BufferDesc  d);
    public void Read       (ResourceHandle h, ResourceState s);
    public void Write      (ResourceHandle h, ResourceState s);
    public void ReadWrite  (ResourceHandle h, ResourceState s);
}

public sealed class RenderGraphCompiler {
    public RenderGraph Compile(IReadOnlyList<RenderPass> passes);
}

public sealed class RenderGraphExecutor : ICommandSink {
    public void BindSwapchain(RhiTexture backBuffer, ResourceHandle h, ResourceState s);
    public void Execute(RenderGraph graph);
}

public enum ResourceState {
    Undefined, RenderTarget, DepthStencil,
    ShaderRead, UnorderedAccess,
    CopySrc, CopyDst, Present,
}

public enum ResourceAccess { Read, Write, ReadWrite }
```

## Barrier inference (MVP1)

For each declared resource, the compiler groups accesses by `ResourceHandle`
and sorts them by registration order. Between consecutive accesses whose
`state` differs, a `BarrierDecl` is emitted on the *earlier* pass's barrier
list. The executor walks passes in order:

1. **Before each pass**: issue the barriers this pass requires
   (`BarriersPerPass[passIdx]`). For Metal these are no-ops; for Vulkan this
   is where real `vkCmdPipelineBarrier` calls will land in Phase 4.
2. **Run pass**: `pass.Execute(sink, ctx)`.
3. **Update current state** of every resource this pass touched.

Reordering **is not** performed in MVP1. The schedule is whatever order
passes were registered in. Cycles are detected at compile time via a
simple "last-access winner" pass; they are not auto-resolved.

## Pairing with the C ABI

| C# | C export |
|---|---|
| `RhiTexture`    | `RhiTexture*` |
| `RhiBuffer`     | `RhiBuffer*`  |
| `RhiPipeline`   | `RpiPipeline*`|
| `BeginRenderPass` | `rhi_begin_render_pass(cl, *desc)` |
| `BindPipeline`  | `rhi_cmd_bind_pipeline(enc, p)` |
| `Draw`          | `rhi_cmd_draw(enc, *drawArgs)`    |
| `RhiBufferUpload` | `rhi_buffer_upload(b, ptr, len)` |

See `docs/rhi/api.md` for the full ABI.

## Task graph

A typical hello-triangle frame:

```
Setup:
    pass[0].Setup builds + writes
        ResourceHandle(scene_color) <- write(RenderTarget)

Execute per frame:
    0. acquire swapchain image
    1. compiler emits pre-pass[0]: implicit barriers for the swapchain image
    2. HelloTrianglePass.Execute:
        - BeginRenderPass(swapchain)
        - BindPipeline(gfx)
        - BindVertexBuffer(0, pos_buf)
        - BindVertexBuffer(1, col_buf)
        - SetViewport(0, 0, w, h)
        - Draw(3)
        - EndPass
    3. present
```

See `Game/HelloTrianglePass.cs` for the actual code and
`Editor/ViewModels/ViewportPanelViewModel.cs` for the Avalonia wiring that
acquires the swapchain image each tick and readback into a `WriteableBitmap`.

## Future work (post-MVP1)

- **Async compute**: split passes between graphics + compute queues.
- **Memory aliasing**: transient resources share physical memory when they
  don't overlap in time.
- **Parallel barriers**: when a barrier is needed, insert it just-in-time
  rather than at every transition.
- **Pass culling**: skip a pass whose outputs aren't consumed.
- **Automatic depth/resolve**: insert MSAA resolve + depth transitions
  when attachments are missing.
