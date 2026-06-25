# RHI C ABI (Phase 2)

> Stable ABI: bumping `ENGINE_ABI_VERSION_RHI` signals a breaking change. See
> `engine_c/rhi/rhi.h` for the live header.

## Responsibilities

- Provide a single C ABI for the Metal RHI (Phase 2) with Vulkan (Phase 4+)
  slotted in behind the same dispatcher.
- All public symbols accept/return POD or opaque `Rhi*` handles. Struct
  descriptors start with a `uint32_t abi` field for forward growth.

## Opacity and handle lifetime

- `RhiDevice`, `RhiSwapchain`, `RhiBuffer`, `RhiTexture`, `RhiShader`,
  `RhiPipeline`, `RhiCommandList`, `RhiEncoder` are all opaque.
- Handles are created by the allocator functions on the active backend and
  disposed/created symmetrically by `rhi_destroy_*`. The dispatcher tracks
  the active backend in `rhi_dispatch.c`. Metal is the only backend compiled
  in Phase 2 (`rhi_metal.mm`).

Opaque-struct-pattern: each `Rhi*` is actually a pointer to a heap-allocated
`Rhi*Impl` struct. The struct owns its Metal counterparts via Objective-C++
ARC `__strong` ivars. `rhi_destroy_*` calls `delete` on the impl, which
releases the underlying Objective-C objects through ARC. The C# side keeps
matching `using` blocks so handles are released deterministically.

## Exports — by category

### Device

```c
int32_t rhi_init(RhiDevice** out_device);
void    rhi_shutdown(RhiDevice* device);
```

### Swapchain

```c
int32_t  rhi_create_swapchain(RhiDevice*, void* os_window_handle,
                               uint32_t w, uint32_t h, RhiSwapchain** out);
void     rhi_destroy_swapchain(RhiSwapchain*);
uint32_t rhi_acquire_next_image(RhiSwapchain*, RhiTexture** out_image);
int32_t  rhi_present(RhiSwapchain*);
```

`os_window_handle` is an `NSWindow*` on macOS. See
`OutOfBand/Engine.CBindings/AvaloniaNativeWindowInterop.cs` for the C#
side that resolves the pointer reflectively.

### Resources

```c
int32_t rhi_create_buffer             (RhiDevice*, const RhiBufferDesc*,             RhiBuffer**);
int32_t rhi_create_texture            (RhiDevice*, const RhiTextureDesc*,            RhiTexture**);
int32_t rhi_create_shader             (RhiDevice*, const RhiShaderDesc*,             RhiShader**);
int32_t rhi_create_graphics_pipeline  (RhiDevice*, const RhiGraphicsPipelineDesc*,  RhiPipeline**);
int32_t rhi_create_compute_pipeline   (RhiDevice*, const RhiComputePipelineDesc*,   RhiPipeline**);

void    rhi_destroy_buffer, rhi_destroy_texture,
         rhi_destroy_shader,   rhi_destroy_pipeline;

int32_t rhi_buffer_upload             (RhiBuffer*, const void* data, uint64_t size);
int32_t rhi_texture_readback          (RhiTexture*, void* out_bytes, uint64_t out_size,
                                        uint32_t out_stride);
```

### Command list + encoders

```c
RhiCommandList* rhi_begin_cmdlist       (RhiDevice*);
int32_t         rhi_submit              (RhiDevice*, RhiCommandList*);
void            rhi_cmd_pipeline_barrier(RhiCommandList*, uint32_t count,
                                          const RhiBarrier*);

RhiEncoder* rhi_begin_render_pass(RhiCommandList*, const RhiPassDesc*);
RhiEncoder* rhi_begin_compute_pass(RhiCommandList*, const char* debug_name);
void        rhi_end_pass         (RhiEncoder*);

void rhi_cmd_bind_pipeline       (RhiEncoder*, RhiPipeline*);
void rhi_cmd_bind_vertex_buffer  (RhiEncoder*, uint32_t slot, RhiBuffer*, uint64_t offset);
void rhi_cmd_bind_uniform_buffer (RhiEncoder*, uint32_t slot, RhiBuffer*);
void rhi_cmd_set_viewport        (RhiEncoder*, float x, float y, float w, float h,
                                  float min_depth, float max_depth);
void rhi_cmd_set_scissor         (RhiEncoder*, uint32_t x, uint32_t y,
                                  uint32_t w, uint32_t h);
void rhi_cmd_set_clear_color     (RhiEncoder*, float r, float g, float b, float a);
void rhi_cmd_draw                (RhiEncoder*, const RhiDrawArgs*);
void rhi_cmd_dispatch            (RhiEncoder*, uint32_t gx, uint32_t gy, uint32_t gz);
```

The lifetime contract: every `rhi_begin_*_pass` is matched by exactly one
`rhi_end_pass`. `rhi_begin_cmdlist` is matched by exactly one `rhi_submit`.
The C# managed wrappers (`engine_cs/Engine.RHI/`) enforce this with the
`CommandRecorder` class: `Submit()` runs once at the end of a frame.

## Backend registration

`rhi_backend_register(const RhiBackend*)` is called at C constructor time
by each compiled-in backend. The dispatcher sets the active backend to the
first registered backend named `"metal"`. Adding Vulkan in Phase 4 will
ship `rhi_vulkan.c` that calls `rhi_backend_register` with the same vtable
shape.

## Resource state tracking

State tracking happens in C# (the render graph compiler). The C side accepts
`RhiBarrier` descriptors but on Metal currently treats them as no-ops. This
preserves the ABI slot for Vulkan integration without changing the rendered
output on Metal.

## Phase 2 entry point

The Avalonia viewport panel calls:

1. `AvaloniaNativeWindowInterop.GetMacOsWindowPointer(window)` -> `IntPtr`
2. `new RhiDevice()` -> calls `rhi_init` (Metal)
3. `device.CreateSwapchain(nsWindow, w, h)` -> `rhi_create_swapchain`
4. `swap.TryAcquireNextImage(out image)` -> `rhi_acquire_next_image`
5. `renderer.RenderFrame(image)` (pass graph compiles each frame)
6. `image.Readback(...)` -> `rhi_texture_readback`
7. `swap.Present()` -> `rhi_present`
