# Metal Backend

> **TODO(rhi):** Implementation notes for the Metal backend. Mesh-shader, persistent device, immutable resources, triple-buffered ring buffers.

The Metal backend targets Apple Silicon baseline (Metal 3). Mesh-shader primitive is required for the Forward+ culling kernel.

See [`engine-spec.md` §21 - risks](../../engine-spec.md) for the Metal-only assumptions guidance.

## Memory Management & ARC

The Metal backend relies on Objective-C Automatic Reference Counting (ARC) to manage the lifetime of Cocoa/Metal API objects (such as `MTLCommandBuffer`, `MTLRenderPassDescriptor`, and `id<MTLTexture>`). 
Every RHI object returned by the backend is a C++ wrapper struct (e.g., `RhiCommandListImpl`) carrying `__strong` Objective-C variables. 

- **Requirement**: The native library target `EngineC` must compile all Objective-C and Objective-C++ files (`.mm`) with the `-fobjc-arc` compiler flag.
- **Lifetime**: Struct objects are allocated on the heap (e.g., `new RhiCommandListImpl()`). When they are deleted (e.g., `delete cli`), ARC automatically releases their strong references to the underlying Metal objects.
