/* SPDX-License-Identifier: MIT */
#ifndef ENGINE_RHI_H
#define ENGINE_RHI_H

/**
 * RHI (Render Hardware Interface) - public C ABI.
 * See docs/rhi/api.md for the full surface and docs/renderer/render-graph.md
 * for how C# orchestrates passes through this surface.
 *
 * Stable ABI: bump `ENGINE_ABI_VERSION` on breaking changes.
 *
 * Opaque handles are POD-compatible across language boundaries. Backends
 * (Metal .mm, Vulkan stub) implement the surface; rhi_dispatch.c forwards
 * each ENGINE_API call to the active RhiBackendVTable.
 */

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#ifndef ENGINE_API
#  ifdef _WIN32
#    define ENGINE_API __declspec(dllimport)
#  else
#    define ENGINE_API __attribute__((visibility("default")))
#  endif
#endif

#define ENGINE_ABI_VERSION_RHI 4

typedef struct RhiDevice         RhiDevice;
typedef struct RhiSwapchain      RhiSwapchain;
typedef struct RhiBuffer         RhiBuffer;
typedef struct RhiTexture        RhiTexture;
typedef struct RhiSampler        RhiSampler;
typedef struct RhiShader         RhiShader;
typedef struct RhiPipeline       RhiPipeline;
typedef struct RhiCommandList    RhiCommandList;
typedef struct RhiEncoder        RhiEncoder;
typedef struct RhiHeap           RhiHeap;
typedef struct RhiFence          RhiFence;
typedef struct RhiBindlessHeap   RhiBindlessHeap;

/** Stable resource handle used by the C# render graph. u64 = (generation << 32) | slot_index. */
typedef uint64_t RhiResourceHandle;

typedef enum RhiQueueType {
    RHI_QUEUE_GRAPHICS = 0,
    RHI_QUEUE_COMPUTE = 1
} RhiQueueType;

/**
 * Texture formats. Additions are binary-compatible; unknown values fall through
 * on consumers that don't know the new code.
 *
 * Uncompressed formats 0-6. Block-compressed formats start at 20 so an enum
 * value added later in the uncompressed range (e.g. Vulkan format 7-19
 * eventually landing here) doesn't collide with a new compressed entry.
 */
typedef enum RhiTextureFormat {
    RHI_FORMAT_UNDEFINED             = 0,
    RHI_FORMAT_RGBA8_UNORM           = 1,
    RHI_FORMAT_RGBA8_SRGB            = 2,
    RHI_FORMAT_RGBA16_FLOAT          = 3,
    RHI_FORMAT_BGRA8_UNORM           = 4,
    RHI_FORMAT_DEPTH32_FLOAT         = 5,
    RHI_FORMAT_DEPTH24_STENCIL8      = 6,
    RHI_FORMAT_BC1_RGB_UNORM_BLOCK   = 20,
    RHI_FORMAT_BC1_RGBA_UNORM_BLOCK  = 21,
    RHI_FORMAT_BC3_UNORM_BLOCK       = 22,
    RHI_FORMAT_BC5_UNORM_BLOCK       = 23,
    RHI_FORMAT_BC7_UNORM_BLOCK       = 24,
    RHI_FORMAT_ETC2_RGB8_UNORM_BLOCK = 25,
    RHI_FORMAT_ASTC_4x4_UNORM_BLOCK  = 26,
} RhiTextureFormat;

typedef enum RhiPrimitiveTopology {
    RHI_TOPOLOGY_TRIANGLE_LIST = 0,
    RHI_TOPOLOGY_LINE_LIST     = 1,
} RhiPrimitiveTopology;

typedef enum RhiBufferUsage {
    RHI_BUFFER_VERTEX         = 1u << 0,
    RHI_BUFFER_USAGE_INDEX    = 1 << 1,
    RHI_BUFFER_USAGE_UNIFORM  = 1 << 2,
    RHI_BUFFER_USAGE_STORAGE  = 1 << 3,
    RHI_BUFFER_USAGE_INDIRECT = 1 << 4,
} RhiBufferUsage;

typedef enum RhiShaderStage {
    RHI_STAGE_VERTEX   = 1u << 0,
    RHI_STAGE_FRAGMENT = 1u << 1,
    RHI_STAGE_COMPUTE  = 1u << 2,
} RhiShaderStage;

typedef enum RhiLoadOp {
    RHI_LOAD_OP_LOAD     = 0,
    RHI_LOAD_OP_CLEAR    = 1,
    RHI_LOAD_OP_DISCARD  = 2,
} RhiLoadOp;

typedef enum RhiStoreOp {
    RHI_STORE_OP_STORE   = 0,
    RHI_STORE_OP_DISCARD = 1,
} RhiStoreOp;

/** Resource states tracked by the C# render graph's barrier inference. */
typedef enum RhiResourceState {
    RHI_STATE_UNDEFINED          = 0,
    RHI_STATE_RENDER_TARGET      = 1,
    RHI_STATE_DEPTH_STENCIL      = 2,
    RHI_STATE_SHADER_READ        = 3,
    RHI_STATE_UNORDERED_ACCESS   = 4,
    RHI_STATE_COPY_SRC           = 5,
    RHI_STATE_COPY_DST           = 6,
    RHI_STATE_PRESENT            = 7,
} RhiResourceState;

/* ----- Resource descriptors ----- */

typedef struct RhiTextureDesc {
    uint32_t          abi;
    uint32_t          width;
    uint32_t          height;
    uint32_t          mip_levels;
    RhiTextureFormat  format;
    uint32_t          usage_flags;       /* RHI_TEXTURE_RENDER_TARGET | SHADER_READ | COPY_* */
} RhiTextureDesc;

#define RHI_TEXTURE_RENDER_TARGET (1u << 0)
#define RHI_TEXTURE_SHADER_READ    (1u << 1)
#define RHI_TEXTURE_COPY_SRC       (1u << 2)
#define RHI_TEXTURE_COPY_DST       (1u << 3)

typedef struct RhiBufferDesc {
    uint32_t abi;
    uint64_t size;
    uint32_t usage_flags;
} RhiBufferDesc;

typedef struct RhiShaderDesc {
    uint32_t          abi;
    uint32_t          stage_flags;
    const char*       source;
    uint32_t          source_len;
    const char*       entry_point;
} RhiShaderDesc;

typedef struct RhiGraphicsPipelineDesc {
    uint32_t          abi;
    RhiShader*        vertex_shader;
    RhiShader*        fragment_shader;
    RhiTextureFormat  color_attachment_format;
    int32_t           enable_depth;
    int32_t           enable_blend;
    int32_t           sample_count;       /* MSAA; 1 = off */
    uint32_t          primitive_topology; /* RhiPrimitiveTopology */
} RhiGraphicsPipelineDesc;

typedef struct RhiComputePipelineDesc {
    uint32_t   abi;
    RhiShader* compute_shader;
} RhiComputePipelineDesc;

typedef struct RhiHeapDesc {
    uint32_t abi;
    uint64_t size;
    uint32_t usage_flags; // e.g. RHI_HEAP_USAGE_RENDER_TARGET | RHI_HEAP_USAGE_SHADER_READ
} RhiHeapDesc;

/* Bindless heap descriptor. capacity=0 requests the device's natural cap
 * (e.g. MTLDevice.maxArgumentBufferSamplerCount on Metal 13+); falls back to
 * 65536 on older platforms. ABI is forward-growth. */
typedef struct RhiBindlessHeapDesc {
    uint32_t abi;
    uint32_t capacity;
} RhiBindlessHeapDesc;

#define RHI_HEAP_USAGE_RENDER_TARGET (1u << 0)
#define RHI_HEAP_USAGE_SHADER_READ    (1u << 1)
#define RHI_HEAP_USAGE_STORAGE        (1u << 2)

/* ----- Render-pass attachment + barrier descriptors ----- */

typedef struct RhiPassAttachment {
    RhiTexture*      texture;
    RhiLoadOp        load_op;
    RhiStoreOp       store_op;
    uint32_t         mip_level;
} RhiPassAttachment;

typedef struct RhiPassDesc {
    uint32_t               abi;
    RhiPassAttachment*     color_attachments;
    uint32_t               color_count;
    RhiPassAttachment*     depth_attachment;        /* optional, NULL if not used */
} RhiPassDesc;

typedef struct RhiBarrier {
    RhiResourceHandle     resource;
    uint32_t              padding;
    RhiResourceState      state_before;
    RhiResourceState      state_after;
} RhiBarrier;

typedef struct RhiDrawArgs {
    uint32_t abi;
    uint32_t vertex_count;
    uint32_t instance_count;
    uint32_t first_vertex;
    uint32_t first_instance;
} RhiDrawArgs;

typedef struct RhiDrawIndexedArgs {
    uint32_t abi;
    uint32_t index_count;
    uint32_t instance_count;
    uint32_t first_index;
    int32_t  vertex_offset;
    uint32_t first_instance;
} RhiDrawIndexedArgs;

typedef struct RhiDrawIndirectArgs {
    uint32_t abi;
    RhiBuffer* indirect_buffer;
    uint64_t   indirect_buffer_offset;
    uint32_t   draw_count;
    uint32_t   stride;
} RhiDrawIndirectArgs;

typedef struct RhiDrawIndexedIndirectArgs {
    uint32_t abi;
    RhiBuffer* indirect_buffer;
    uint64_t   indirect_buffer_offset;
    uint32_t   draw_count;
    uint32_t   stride;
} RhiDrawIndexedIndirectArgs;

/* ----- Device ----- */

ENGINE_API int32_t  rhi_init(RhiDevice** out_device);
ENGINE_API void     rhi_shutdown(RhiDevice* device);

/* ----- Swapchain ----- */

/* os_window_handle is a platform surface handle. On macOS the Metal backend
 * interprets it as an `NSView*` (the view into which a `CAMetalLayer` sublayer
 * is attached). Win32 (Vulkan) treats it as HWND; X11/Linux treats it as
 * xcb_window_t. Callers are expected to pass the platform-correct type
 * matching this declaration's ABI.
 */
ENGINE_API int32_t  rhi_create_swapchain(RhiDevice* device,
                                         void* os_window_handle,
                                         uint32_t width, uint32_t height,
                                         RhiSwapchain** out_sc);

/* ----- macOS Metal embed helpers -----
 *
 * rhi_create_macos_metal_view / rhi_destroy_macos_metal_view are Metal-only
 * helpers used to embed a CAMetalLayer-bearing NSView inside an Avalonia
 * NativeControlHost (or any other framework that needs a discrete, child
 * NSView backed by a Metal layer). rhi_destroy_macos_metal_view removes the
 * NSView from its superview and releases the layer; the returned void* from
 * rhi_create_macos_metal_view is `__bridge_retained` so the caller owns one
 * strong reference until destroy.
 *
 * On non-Apple platforms these return NULL / become no-ops. They are
 * exported through the dispatcher for ABI uniformity.
 */
ENGINE_API void*    rhi_create_macos_metal_view(void* parent_view_handle,
                                                uint32_t width, uint32_t height);
ENGINE_API void     rhi_destroy_macos_metal_view(void* view_handle);
ENGINE_API void     rhi_destroy_swapchain(RhiSwapchain* sc);
ENGINE_API uint32_t rhi_acquire_next_image(RhiSwapchain* sc,
                                            RhiTexture** out_image);
ENGINE_API int32_t  rhi_present(RhiSwapchain* sc);
ENGINE_API void     rhi_swapchain_get_size(RhiSwapchain* sc,
                                           uint32_t* out_width,
                                           uint32_t* out_height);

/* ----- Resources ----- */

ENGINE_API int32_t  rhi_create_buffer(RhiDevice* device,
                                      const RhiBufferDesc* desc,
                                      RhiBuffer** out_buf);
ENGINE_API int32_t  rhi_create_texture(RhiDevice* device,
                                       const RhiTextureDesc* desc,
                                       RhiTexture** out_tex);
ENGINE_API int32_t  rhi_create_shader(RhiDevice* device,
                                      const RhiShaderDesc* desc,
                                      RhiShader** out_shader);
ENGINE_API int32_t  rhi_create_graphics_pipeline(RhiDevice* device,
                                                 const RhiGraphicsPipelineDesc* desc,
                                                 RhiPipeline** out_pipe);
ENGINE_API int32_t  rhi_create_compute_pipeline(RhiDevice* device, const RhiComputePipelineDesc* desc, RhiPipeline** out);

ENGINE_API int32_t  rhi_create_fence(RhiDevice* device, RhiFence** out);

ENGINE_API int32_t  rhi_create_heap(RhiDevice* device,
                                    const RhiHeapDesc* desc,
                                    RhiHeap** out_heap);
ENGINE_API int32_t  rhi_create_texture_from_heap(RhiDevice* device,
                                                 RhiHeap* heap,
                                                 const RhiTextureDesc* desc,
                                                 uint64_t offset,
                                                 RhiTexture** out_tex);
ENGINE_API int32_t  rhi_create_buffer_from_heap(RhiDevice* device,
                                                RhiHeap* heap,
                                                const RhiBufferDesc* desc,
                                                uint64_t offset,
                                                RhiBuffer** out_buf);

ENGINE_API RhiSampler* rhi_create_sampler(RhiDevice* dev);
ENGINE_API void rhi_destroy_sampler(RhiSampler* samp);
ENGINE_API void     rhi_destroy_shader(RhiShader* sh);
ENGINE_API void rhi_destroy_pipeline(RhiPipeline* pipeline);
ENGINE_API void rhi_destroy_heap(RhiHeap* heap);
ENGINE_API void rhi_destroy_fence(RhiFence* fence);

/* ----- Bindless heap -----
 *
 * Stable, runtime-sized sampler/texture descriptor table. Slang/shader-side
 * declarations of the form `Texture2D textures[] ` inside a [binding(N,0)]
 * ParameterBlock can be backed by this heap; shaders then index
 * `bindless.textures[idx]` from a uniform-supplied id.
 *
 * CPU-side slot allocator (R/W): register/release/lookup map a stable id
 * (a uint32 slot) to/from an RhiTexture. GPU-side bind (R): attach the heap's
 * resident argument buffer + texture residency list to a command encoder at a
 * target binding slot.
 *
 * Coexists with the legacy `rhi_cmd_bind_texture_array` API — bindless heaps
 * are additive and do not break existing callers. */
ENGINE_API int32_t rhi_create_bindless_heap(RhiDevice* device,
                                            const RhiBindlessHeapDesc* desc,
                                            RhiBindlessHeap** out_heap);
ENGINE_API void    rhi_destroy_bindless_heap(RhiBindlessHeap* heap);

/* Allocates the next free slot. Returns 0 + slot in *out_slot on success,
 * -1 if the heap is full. Repeat calls with the same RhiTexture* return the
 * same slot (a stable map is maintained). */
ENGINE_API int32_t rhi_bindless_register_texture(RhiBindlessHeap* heap,
                                                 RhiTexture* texture,
                                                 uint32_t* out_slot);

/* Releases the slot; subsequent register calls reuse it. */
ENGINE_API void    rhi_bindless_release_texture(RhiBindlessHeap* heap,
                                                 uint32_t slot);

/* Reverse lookup: texture* -> slot. Returns 0 on hit, -1 on miss. */
ENGINE_API int32_t rhi_bindless_lookup_slot(RhiBindlessHeap* heap,
                                            RhiTexture* texture,
                                            uint32_t* out_slot);

ENGINE_API uint64_t rhi_get_buffer_device_address(RhiBuffer* buf);

/* ----- Data transfer ----- */
ENGINE_API int32_t  rhi_buffer_upload(RhiBuffer* buf, const void* data, uint64_t size);

/** Read a texture back to CPU bytes. Used for Avalonia viewport display. */
ENGINE_API int32_t  rhi_texture_readback(RhiTexture* tex,
                                         void* out_bytes, uint64_t out_size,
                                         uint32_t out_stride);

ENGINE_API int32_t  rhi_texture_upload(RhiTexture* tex, const void* data,
                                       uint64_t size, uint32_t stride);

/** Upload one mip level of a texture (block-compressed or uncompressed).
 * Stride is the byte size of one row of blocks (for compressed) or pixels
 * (for uncompressed). Destination is mipmapLevel=`mip_level`. */
ENGINE_API int32_t  rhi_texture_upload_mip(RhiTexture* tex, uint32_t mip_level,
                                            const void* data, uint64_t size,
                                            uint32_t stride);

/** Block dimensions for a compressed format. Returns block_width, block_height,
 * and bytes_per_block via the out parameters. Returns 0 on an uncompressed
 * format or an unknown one. */
ENGINE_API void     rhi_format_block_info(RhiTextureFormat fmt,
                                            uint32_t* out_block_w,
                                            uint32_t* out_block_h,
                                            uint32_t* out_bytes_per_block);

/* ----- Command list ----- */

ENGINE_API RhiCommandList* rhi_begin_cmdlist(RhiDevice* device, RhiQueueType queue);
ENGINE_API int32_t         rhi_submit(RhiDevice* device, RhiCommandList* cmdlist);
ENGINE_API void            rhi_cmd_pipeline_barrier(RhiCommandList* cl,
                                                   uint32_t count,
                                                   const RhiBarrier* barriers);

ENGINE_API void            rhi_cmd_signal_fence(RhiCommandList* cl, RhiFence* fence, uint64_t value);
ENGINE_API void            rhi_cmd_wait_fence(RhiCommandList* cl, RhiFence* fence, uint64_t value);

/* Inside a cmdlist, encoders model render-pass + compute-pass scopes. */
ENGINE_API RhiEncoder*     rhi_begin_render_pass(RhiCommandList* cl,
                                                 const RhiPassDesc* desc);
ENGINE_API RhiEncoder* rhi_begin_compute_pass(RhiCommandList* cl,
                                              const char* debug_name);
ENGINE_API void        rhi_end_pass(RhiEncoder* enc);

ENGINE_API void rhi_cmd_bind_pipeline(RhiEncoder* enc, RhiPipeline* p);
ENGINE_API void rhi_cmd_bind_vertex_buffer(RhiEncoder* enc,
                                            uint32_t slot, RhiBuffer* buf,
                                            uint64_t offset);
ENGINE_API void rhi_cmd_bind_uniform_buffer(RhiEncoder* enc,
                                            uint32_t slot, RhiBuffer* buf);
ENGINE_API void rhi_cmd_set_viewport(RhiEncoder* enc,
                                     float x, float y,
                                     float w, float h,
                                     float min_depth, float max_depth);
ENGINE_API void rhi_cmd_set_scissor(RhiEncoder* enc,
                                    uint32_t x, uint32_t y,
                                    uint32_t w, uint32_t h);
ENGINE_API void rhi_cmd_set_clear_color(RhiEncoder* enc,
                                        float r, float g, float b, float a);
ENGINE_API void rhi_cmd_push_constants(RhiEncoder* enc,
                                       uint32_t slot,
                                       uint32_t size, const void* data);
ENGINE_API void rhi_cmd_draw(RhiEncoder* enc, const RhiDrawArgs* args);
ENGINE_API void rhi_cmd_draw_indirect(RhiEncoder* enc, const RhiDrawIndirectArgs* args);
ENGINE_API void rhi_cmd_draw_indexed(RhiEncoder* enc, const RhiDrawIndexedArgs* args);
ENGINE_API void rhi_cmd_draw_indexed_indirect(RhiEncoder* enc, const RhiDrawIndexedIndirectArgs* args);
ENGINE_API void rhi_cmd_bind_index_buffer(RhiEncoder* enc, RhiBuffer* buf,
                                          int32_t is_32bit, uint64_t offset);
ENGINE_API void rhi_cmd_bind_texture(RhiEncoder* enc, uint32_t slot, RhiTexture* tex);
ENGINE_API void rhi_cmd_bind_sampler(RhiEncoder* enc, uint32_t slot, RhiSampler* samp);
ENGINE_API void rhi_cmd_use_buffer(RhiEncoder* enc, RhiBuffer* buf, uint32_t usage);
ENGINE_API void rhi_cmd_bind_texture_array(RhiEncoder* enc, uint32_t slot, RhiTexture** texs, uint32_t count);
ENGINE_API void rhi_cmd_bind_bindless_heap(RhiEncoder* enc, RhiBindlessHeap* heap, uint32_t slot);
ENGINE_API void rhi_cmd_dispatch(RhiEncoder* enc,
                                 uint32_t groups_x, uint32_t groups_y,
                                 uint32_t groups_z);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* ENGINE_RHI_H */
