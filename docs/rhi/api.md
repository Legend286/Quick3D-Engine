# RHI C ABI (Phase 2)

> Stable ABI: bumping `ENGINE_ABI_VERSION_RHI` signals a breaking change. See
> `engine_c/rhi/rhi.h` for the live header.

## Responsibilities

- Provide a single C ABI for the Metal RHI (Phase 2) with Vulkan (Phase 4+)
  slotted in behind the same dispatcher.
- All public symbols accept/return POD or opaque `Rhi*` handles. Struct
  descriptors start with a `uint32_t abi` field for forward growth.

The managed `IGameLoop.RenderThumbnail` and `IGameLoop.LoadModelPreview`
methods accept an optional model part index. `-1` renders the complete model;
a non-negative value renders the matching stable `.mdl` part.

`IGameLoop.RendererMode` selects clustered Forward+ raster rendering or path
tracing. `RendererModeChanged` keeps editor chrome synchronized with changes
from keyboard shortcuts. Renderer implementations cache one compiled graph
and its pass-owned state per mode, so activating a previously used mode does
not recompile graph topology.

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

`rhi_get_buffer_device_address(NULL)` returns zero at both the dispatcher and
backend boundary. Managed buffer address, upload, and readback operations
reject a released handle with `ObjectDisposedException` before entering native
code. Buffer creation also rejects a successful status paired with a null
handle. These checks allow the renderer frame boundary to report a stale
resource lifetime error instead of terminating the process with a native null
dereference.

Textures created with `RHI_TEXTURE_EXTERNAL_IMAGE` add one more lifetime edge:
the texture may export a platform-owned external image handle for compositor
interop. On macOS that handle is an `IOSurfaceRef`. The caller keeps owning the
`RhiTexture`; the exported handle is released independently through
`rhi_release_external_image_handle`.

Depth textures created with both `RHI_TEXTURE_RENDER_TARGET` and
`RHI_TEXTURE_SHADER_READ` may be rendered and sampled in later passes. A
depth-only `RhiPassDesc` sets `color_count` to zero and supplies only
`depth_attachment`; this maps directly to a Metal render pass without a color
attachment and to a Vulkan depth-only rendering scope.

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
void     rhi_swapchain_get_size(RhiSwapchain*, uint32_t* width, uint32_t* height);
```

`os_window_handle` is an opaque platform surface pointer. On macOS the
Metal backend interprets it as an `NSView*` and attaches a
`CAMetalLayer` as a sublayer of the view's `layer`. Native Win32
(Vulkan) treats it as `HWND`; X11 Linux treats it as
`xcb_window_t`. The Editor wires up the macOS case via the embed
helpers listed below.

### macOS Metal embed helpers

```c
void* rhi_create_macos_metal_view(void* parent_view_handle,
                                    uint32_t width, uint32_t height);
void  rhi_destroy_macos_metal_view(void* view_handle);
```

These allocate and release an `NSView` that hosts a
`CAMetalLayer`-compatible layer hierarchy. Used by the Editor
to embed a Metal-backed surface inside an Avalonia
`NativeControlHost`. The returned `void*` carries one strong
reference (`__bridge_retained`); the caller owns that
reference until `rhi_destroy_macos_metal_view` is invoked.
On non-Apple platforms these return `NULL` / become no-ops.

See `OutOfBand/Engine.CBindings/AvaloniaNativeWindowInterop.cs`
for the C# wrappers and the platform-handle reflective lookups.

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
int32_t rhi_texture_export_external_image(RhiTexture*, void** out_handle,
                                          uint32_t* out_width, uint32_t* out_height,
                                          RhiTextureFormat* out_format);
void    rhi_release_external_image_handle(void* handle);
```

`RhiGraphicsPipelineDesc.depth_compare` selects `LESS_EQUAL` for normal depth
testing or `ALWAYS` for operations such as scissored shadow-atlas tile clears.
The compare mode is pipeline state and is rebound with the graphics pipeline.

Descriptor ABI 5 appends three color formats and an explicit attachment count.
ABI 4 callers retain the original single `color_attachment_format` behavior.
`RhiPipeline.CreateGraphicsMrt` currently exposes the two-target managed path;
the native descriptor reserves four targets. `RHI_FORMAT_RG32_UINT` stores exact
integer identifiers and `RHI_FORMAT_RG16_UNORM` stores two normalized 16-bit
channels for visibility-buffer barycentrics.
`RhiTexture.GetUncompressedBytesPerPixel` reports the byte width used by
allocation and render-graph diagnostics, returning zero for compressed or
undefined formats.

`rhi_texture_export_external_image` is intended for editor/compositor interop,
not general gameplay readback. The current Metal implementation supports
BGRA8 render targets only and expects the source texture to be created with
`RHI_TEXTURE_EXTERNAL_IMAGE`.

### Command list + encoders

```c
RhiCommandList* rhi_begin_cmdlist       (RhiDevice*);
int32_t         rhi_submit              (RhiDevice*, RhiCommandList*);
int32_t         rhi_submit_and_wait     (RhiDevice*, RhiCommandList*);
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

Managed code uses `RhiPipeline.CreateDepthOnly`,
`RhiTexture.CreateDepth(..., shaderReadable: true)`, and
`CommandRecorder.BeginDepthOnlyPass` for portable shadow-map rendering.
`CommandRecorder.BeginRenderPass(ReadOnlySpan<RhiTexture>, ...)` records two to
four color attachments with shared load/store operations and an optional depth
attachment.

### Timestamp queries

```c
int32_t rhi_create_timestamp_query_pool(
    RhiDevice* device, uint32_t sample_count,
    RhiTimestampQueryPool** out_pool);
int32_t rhi_timestamp_query_pool_set_samples_per_duration(
    RhiTimestampQueryPool* pool, uint32_t sample_count);
void rhi_destroy_timestamp_query_pool(RhiTimestampQueryPool* pool);
int32_t rhi_cmd_write_timestamp(
    RhiCommandList* command_list, RhiTimestampQueryPool* pool,
    uint32_t sample_index);
int32_t rhi_cmd_resolve_timestamps(
    RhiCommandList* command_list, RhiTimestampQueryPool* pool,
    uint32_t sample_count);
int32_t rhi_timestamp_query_pool_read_durations(
    RhiTimestampQueryPool* pool, uint32_t duration_count,
    uint64_t* out_duration_nanoseconds);
int32_t rhi_timestamp_query_pool_read_frame_duration(
    RhiTimestampQueryPool* pool,
    uint64_t* out_duration_nanoseconds);
```

Pools default to one adjacent pair per duration. The renderer configures 64
samples for duration `i` through
`rhi_timestamp_query_pool_set_samples_per_duration`. Metal assigns adjacent
begin/end pairs within that block to the pass's internal encoders and reduces
the valid pairs into one duration. Reads are non-blocking: `1` means ready, `0`
means still in flight, and `-1` means unsupported or invalid. Metal prefers
explicit draw- and dispatch-boundary samples, falling back to stage-boundary
attachments only when explicit boundary sampling is unavailable. Metal
correlates CPU and GPU clocks before submission and after completion, then
converts each GPU delta using the measured clock-span ratio. The frame-duration
read uses Metal's completed command-buffer GPU start and end times as a raw wall
span. Vulkan can map the same API to query pools and `timestampPeriod` while
retaining the logical duration contract.

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

`CommandRecorder.UseBuffer` declares residency for resources accessed through
bindings or GPU virtual addresses. Its `usage` value contains read (`1`), write
(`2`), or both (`3`); it is not a shader binding index. The managed RHI rejects
zero and unknown bits before backend command encoding.

## Phase 2 entry point

The Avalonia viewport panel calls:

1. `ViewportMetalLayerHost` instantiates a child `NSView` via
   `AvaloniaNativeWindowInterop.CreateMacosMetalView(parent, w, h)`
   -> `rhi_create_macos_metal_view`.
2. `new RhiDevice()` -> calls `rhi_init` (Metal).
3. `device.CreateSwapchain(nsView, w, h)` -> `rhi_create_swapchain`
   attaches a `CAMetalLayer` sublayer to the host `NSView`.
4. `swap.TryAcquireNextImage(out image)` -> `rhi_acquire_next_image`
   returns a fresh `CAMetalDrawable` backed by a `MTLTexture`.
5. `renderer.RenderFrame(image, w, h)` compiles/runs the pass graph
   once per frame; hello-triangle encodes the draw on the
   back-buffer acquired in (4).
6. `swap.Present()` -> `rhi_present` (commits the command buffer
   and CoreAnimation flips the drawable at the next vsync).

### Editor viewport state

`IGameLoop` exposes renderer, projection, debug-view, field-of-view, and
orthographic-size state to the editor shell. These settings remain above the C
ABI because they select existing render plans and camera constants rather than
creating backend resources. `ViewportProjectionMode` supports Perspective and
Orthographic; `ViewportDebugView` selects Lit, Wireframe, Depth, normal,
material-channel, lighting, position, emissive, UV, tangent, and bitangent
visualizations, plus visibility-buffer identifier/barycentric and reconstructed
position, normal, UV, material, instance, and tangent diagnostics. Visibility
PBR compares the full raster path with 8×8 compute shading and amplified error.

`IGameLoop.HasPendingRenderWork` reports whether renderer-owned incremental
work needs another viewport frame. Low-power editor presentation consults it
after normal input bursts, allowing workloads such as a DDGI scene bake to
finish without forcing the viewport into permanent realtime mode.

Projection changes animate through a shared camera projection blend. The blend
is carried in camera frame data and used by raster shaders, path-traced primary
rays, picking, outlines, and overlays, so no render-graph recompilation or
backend-specific projection path is required.

### Advanced Pipeline Features (Phase 3+)

```c
// Push constants (inlined buffer updates)
void rhi_push_constants(RhiEncoder* encoder, uint32_t size, const void* data);

// Compute and Indirect Drawing
void rhi_cmd_dispatch_indirect(RhiEncoder* encoder, RhiBuffer* indirect_buffer, uint64_t offset);
void rhi_cmd_draw_indirect(RhiEncoder* encoder, RhiBuffer* indirect_buffer, uint64_t offset, uint32_t draw_count, uint32_t stride);

// Samplers and Bindless Heaps
int32_t rhi_create_sampler(RhiDevice* device, const RhiSamplerDesc* desc, RhiSampler** out_sampler);
void    rhi_destroy_sampler(RhiSampler* sampler);

int32_t rhi_create_heap(RhiDevice* device, const RhiHeapDesc* desc, RhiHeap** out_heap);
void    rhi_destroy_heap(RhiHeap* heap);

// Fences for GPU/CPU sync
int32_t rhi_create_fence(RhiDevice* device, RhiFence** out_fence);
void    rhi_destroy_fence(RhiFence* fence);
uint64_t rhi_fence_get_completed_value(RhiFence* fence);
void    rhi_cmd_signal_fence(RhiCommandList* cmd, RhiFence* fence,
                             uint64_t value);
int32_t rhi_buffer_read_mapped(RhiBuffer* buffer, uint64_t offset,
                               void* output, uint64_t size);
int32_t rhi_cmd_copy_texture_to_buffer(RhiCommandList* cmd,
                                       RhiTexture* source,
                                       uint32_t source_x,
                                       uint32_t source_y,
                                       uint32_t width,
                                       uint32_t height,
                                       uint32_t source_mip_level,
                                       RhiBuffer* destination,
                                       uint64_t destination_offset,
                                       uint32_t destination_bytes_per_row);
```

`rhi_fence_get_completed_value` and `rhi_buffer_read_mapped` never submit or
wait for GPU work. Callers first record a texture-to-buffer copy and timeline
signal, then poll the fence on later frames before reading shared storage.
Metal requires texture-copy row strides and destination offsets aligned to
256 bytes; the command returns an error when the region, format, alignment,
or destination range is invalid.

`CommandRecorder.BeginTimestampScope` and
`CommandRecorder.EndTimestampScope` bracket one logical render-graph pass.
The Metal backend gives each logical pass a fixed sample block and assigns a
unique pair to every internal encoder. Stage-only render timing sums separate
vertex and fragment pairs; compute and explicit draw/dispatch timing use one
pair per encoder.

`GpuResourceRegistry` records live committed managed RHI allocations.
`RhiBuffer.SetDebugName` and `RhiTexture.SetDebugName` attach diagnostic names
and categories. Heap-backed wrappers remain visible through graph declarations
but do not register duplicate committed allocations.
