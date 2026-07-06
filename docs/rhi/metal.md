# Metal Backend

> **TODO(rhi):** Implementation notes for the Metal backend. Mesh-shader, persistent device, immutable resources, triple-buffered ring buffers.

The Metal backend targets Apple Silicon baseline (Metal 3). Mesh-shader primitive is required for the Forward+ culling kernel.

See [`engine-spec.md` §21 - risks](../../engine-spec.md) for the Metal-only assumptions guidance.

## Memory Management & ARC

The Metal backend relies on Objective-C Automatic Reference Counting (ARC) to manage the lifetime of Cocoa/Metal API objects (such as `MTLCommandBuffer`, `MTLRenderPassDescriptor`, and `id<MTLTexture>`). 
Every RHI object returned by the backend is a C++ wrapper struct (e.g., `RhiCommandListImpl`) carrying `__strong` Objective-C variables. 

- **Requirement**: The native library target `EngineC` must compile all Objective-C and Objective-C++ files (`.mm`) with the `-fobjc-arc` compiler flag.
- **Lifetime**: Struct objects are allocated on the heap (e.g., `new RhiCommandListImpl()`). When they are deleted (e.g., `delete cli`), ARC automatically releases their strong references to the underlying Metal objects.

## Blit Pipelines Without Depth

Passing a depth attachment to a `BeginRenderPass` whose pipeline was created with `enableDepth: false` is a **fatal** configuration. Metal validation runs `[encoder validateWithDevice:]` at command-buffer commit time and drops the entire buffer when `MTLRenderPipelineState.depthAttachmentPixelFormat == MTLPixelFormatInvalid` disagrees with a non-null `MTLRenderPassDescriptor.depthAttachment.texture`. The whole frame's compute passes, AS builds, and blits all vanish; the swapchain drawable then presents whatever the underlying IOSurface was initialized to (effectively zero/black on Apple Silicon).

Required pattern for a fullscreen blit / present pass:

```csharp
_blitPipeline = RhiPipeline.CreateGraphics(_device, _blitVs, _blitFs,
    RhiNative.TextureFormat.Bgra8Unorm, enableDepth: false);

// depth=null, DepthLoadOp.Discard, DepthStoreOp.Discard — depth values are unused
sink.BeginRenderPass(colorTarget, RhiNative.LoadOp.Clear, RhiNative.StoreOp.Store,
                     null, RhiNative.LoadOp.Discard, RhiNative.StoreOp.Discard);
```

The load/store operands for the unused depth attachment don't matter — `metal_begin_render_pass` gates the entire depth block on `desc->depth_attachment != nullptr`. `Discard` is preferred so the values compile against the project's `LoadOp`/`StoreOp` enum regardless of platform.
