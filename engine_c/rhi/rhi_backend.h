/* SPDX-License-Identifier: MIT */
#ifndef ENGINE_RHI_BACKEND_H
#define ENGINE_RHI_BACKEND_H

#include "rhi.h"

#ifdef __cplusplus
extern "C" {
#endif

/**
 * Backend vtable. Visible to BOTH the dispatcher (rhi_dispatch.c) and every
 * backend implementation (rhi_metal.mm, future rhi_vulkan.c). Each compiled-in
 * backend registers a `RhiBackend` struct at static-init time; the dispatcher
 * picks the active one and forwards public ENGINE_API calls through it.
 *
 * Backends are not part of the public ABI. Do not export any function whose
 * signature depends on `RhiBackend` - it is purely an internal dispatch
 * mechanism.
 */

typedef struct RhiBackend {
    const char*                 name;
    int32_t  (*init)(RhiDevice** out_device);
    void     (*shutdown)(RhiDevice* device);
    int32_t  (*create_swapchain)(RhiDevice* device, void* os_window_handle,
                                  uint32_t w, uint32_t h, RhiSwapchain** out_sc);
    void     (*destroy_swapchain)(RhiSwapchain* sc);
    uint32_t (*acquire_next_image)(RhiSwapchain* sc, RhiTexture** out_image);
    int32_t  (*present)(RhiSwapchain* sc);
    void     (*swapchain_get_size)(RhiSwapchain* sc, uint32_t* width, uint32_t* height);

    int32_t  (*create_buffer)(RhiDevice* device, const RhiBufferDesc* desc,
                               RhiBuffer** out_buf);
    int32_t  (*create_texture)(RhiDevice* device, const RhiTextureDesc* desc,
                                RhiTexture** out_tex);
    int32_t  (*create_shader)(RhiDevice* device, const RhiShaderDesc* desc,
                               RhiShader** out_shader);
    int32_t  (*create_graphics_pipeline)(RhiDevice* device,
                                          const RhiGraphicsPipelineDesc* desc,
                                          RhiPipeline** out_pipe);
    int32_t  (*create_compute_pipeline)(RhiDevice* device,
                                         const RhiComputePipelineDesc* desc,
                                         RhiPipeline** out_pipe);
    int32_t  (*create_heap)(RhiDevice* d, const RhiHeapDesc* desc, RhiHeap** out);
    int32_t  (*create_texture_from_heap)(RhiDevice* d, RhiHeap* h, const RhiTextureDesc* desc, uint64_t offset, RhiTexture** out);
    RhiSampler* (*create_sampler)(RhiDevice* d);
    void (*destroy_sampler)(RhiSampler* s);
    int32_t  (*create_buffer_from_heap)(RhiDevice* d, RhiHeap* h, const RhiBufferDesc* desc, uint64_t offset, RhiBuffer** out);
    int32_t  (*create_fence)(RhiDevice* d, RhiFence** out);
    void     (*destroy_buffer)(RhiBuffer* buf);
    void     (*destroy_texture)(RhiTexture* tex);
    void     (*destroy_shader)(RhiShader* sh);
    void     (*destroy_pipeline)(RhiPipeline* p);
    void     (*destroy_heap)(RhiHeap* h);
    void     (*destroy_fence)(RhiFence* f);
    int32_t  (*buffer_upload)(RhiBuffer* buf, const void* data, uint64_t size);
    int32_t  (*texture_readback)(RhiTexture* tex, void* out, uint64_t out_size,
                                  uint32_t stride);
    int32_t  (*texture_upload)(RhiTexture* tex, const void* data, uint64_t size, uint32_t stride);
    uint64_t (*get_buffer_device_address)(RhiBuffer* buf);

    RhiCommandList* (*begin_cmdlist)(RhiDevice* device, RhiQueueType queue);
    int32_t         (*submit)(RhiDevice* device, RhiCommandList* cmdl);
    void            (*cmd_pipeline_barrier)(RhiCommandList* cl,
                                            uint32_t count,
                                            const RhiBarrier* barriers);
    void            (*cmd_signal_fence)(RhiCommandList* cl, RhiFence* f, uint64_t value);
    void            (*cmd_wait_fence)(RhiCommandList* cl, RhiFence* f, uint64_t value);
    RhiEncoder*     (*begin_render_pass)(RhiCommandList* cl, const RhiPassDesc* desc);
    RhiEncoder*     (*begin_compute_pass)(RhiCommandList* cl, const char* debug_name);
    void            (*end_pass)(RhiEncoder* enc);

    void (*cmd_bind_pipeline)(RhiEncoder* enc, RhiPipeline* p);
    void (*cmd_bind_vertex_buffer)(RhiEncoder* enc, uint32_t slot,
                                    RhiBuffer* buf, uint64_t offset);
    void (*cmd_bind_uniform_buffer)(RhiEncoder* enc, uint32_t slot,
                                     RhiBuffer* buf);
    void (*cmd_set_viewport)(RhiEncoder* enc, float x, float y, float w, float h,
                              float min_depth, float max_depth);
    void (*cmd_set_scissor)(RhiEncoder* enc, uint32_t x, uint32_t y,
                             uint32_t w, uint32_t h);
    void (*cmd_set_clear_color)(RhiEncoder* enc, float r, float g, float b, float a);
    void (*cmd_push_constants)(RhiEncoder* enc, uint32_t size, const void* data);
    void (*cmd_draw)(RhiEncoder* enc, const RhiDrawArgs* args);
    void (*cmd_draw_indirect)(RhiEncoder* enc, const RhiDrawIndirectArgs* args);
    void (*cmd_draw_indexed)(RhiEncoder* enc, const RhiDrawIndexedArgs* args);
    void (*cmd_draw_indexed_indirect)(RhiEncoder* enc, const RhiDrawIndexedIndirectArgs* args);
    void (*cmd_bind_index_buffer)(RhiEncoder* enc, RhiBuffer* buf, int32_t is_32bit, uint64_t offset);
    void (*cmd_bind_texture)(RhiEncoder* enc, uint32_t slot, RhiTexture* tex);
    void (*cmd_bind_texture_array)(RhiEncoder* enc, uint32_t slot, RhiTexture** texs, uint32_t count);
    void (*cmd_bind_sampler)(RhiEncoder* enc, uint32_t slot, RhiSampler* samp);
    void (*cmd_dispatch)(RhiEncoder* enc, uint32_t gx, uint32_t gy, uint32_t gz);
} RhiBackend;

/* Backends call this at static-init time via constructor attribute. */
void rhi_backend_register(const RhiBackend* backend);

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* ENGINE_RHI_BACKEND_H */
