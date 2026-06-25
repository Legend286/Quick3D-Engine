/* SPDX-License-Identifier: MIT */
#include "engine_log.h"
#include "rhi.h"
#include "rhi_backend.h"

#include <stdlib.h>
#include <string.h>

/**
 * Backend registry. Each compiled-in backend registers a static-init
 * constructor that calls rhi_backend_register(). The dispatcher picks the
 * active backend by name (preferring "metal" on Apple).
 */

#define ENGINE_RHI_BACKEND_MAX 4

static RhiBackend g_backends[ENGINE_RHI_BACKEND_MAX];
static uint32_t    g_backend_count = 0;
static int32_t     g_active = -1;

void rhi_backend_register(const RhiBackend* backend) {
    if (g_backend_count >= ENGINE_RHI_BACKEND_MAX) return;
    g_backends[g_backend_count++] = *backend;
    if (g_active < 0 && strcmp(backend->name, "metal") == 0) {
        g_active = (int32_t)(g_backend_count - 1);
    }
}

/* Forward-declared so the dispatcher entry points below can call it without
 * triggering ISO C99 implicit-function-declaration. Definition is at the
 * bottom of this file. */
static void rhi_ensure_default_backends(void);

__attribute__((constructor))
static void rhi_dispatch_init_log(void) {
    ENGINE_LOG_INFO("rhi", "dispatcher ready (registered=%u)", g_backend_count);
}

/* ----- Dispatcher entry points ----- */

int32_t rhi_init(RhiDevice** out_device) {
    rhi_ensure_default_backends();
    if (g_active < 0) {
        ENGINE_LOG_ERROR("rhi", "no backend registered (build rhi_metal.mm?)");
        return -1;
    }
    RhiBackend* b = &g_backends[g_active];
    if (!b->init) return -1;
    return b->init(out_device);
}
void rhi_shutdown(RhiDevice* device) {
    if (g_active < 0) return;
    RhiBackend* b = &g_backends[g_active];
    if (b->shutdown) b->shutdown(device);
}

/* rhi_create_swapchain parameter fix: the previous version had TWO parameters
   named `h` (the void* window handle AND the height uint32_t). Renamed the
   uint32_t to `height_out_arg` and tightened the dispatch. */
int32_t rhi_create_swapchain(RhiDevice* d, void* os_win_handle,
                              uint32_t width, uint32_t height,
                              RhiSwapchain** out_swapchain) {
    return g_backends[g_active].create_swapchain(
        d, os_win_handle, width, height, out_swapchain);
}

void rhi_destroy_swapchain(RhiSwapchain* sc) { g_backends[g_active].destroy_swapchain(sc); }
uint32_t rhi_acquire_next_image(RhiSwapchain* sc, RhiTexture** out) {
    return g_backends[g_active].acquire_next_image(sc, out);
}
int32_t rhi_present(RhiSwapchain* sc) { return g_backends[g_active].present(sc); }

int32_t rhi_create_buffer(RhiDevice* d, const RhiBufferDesc* desc, RhiBuffer** out) {
    return g_backends[g_active].create_buffer(d, desc, out);
}
int32_t rhi_create_texture(RhiDevice* d, const RhiTextureDesc* desc, RhiTexture** out) {
    return g_backends[g_active].create_texture(d, desc, out);
}
int32_t rhi_create_shader(RhiDevice* d, const RhiShaderDesc* desc, RhiShader** out) {
    return g_backends[g_active].create_shader(d, desc, out);
}
int32_t rhi_create_graphics_pipeline(RhiDevice* d, const RhiGraphicsPipelineDesc* desc, RhiPipeline** out) {
    return g_backends[g_active].create_graphics_pipeline(d, desc, out);
}
int32_t rhi_create_compute_pipeline(RhiDevice* d, const RhiComputePipelineDesc* desc, RhiPipeline** out) {
    return g_backends[g_active].create_compute_pipeline(d, desc, out);
}

void rhi_destroy_buffer(RhiBuffer* b) { g_backends[g_active].destroy_buffer(b); }
void rhi_destroy_texture(RhiTexture* t) { g_backends[g_active].destroy_texture(t); }
void rhi_destroy_shader(RhiShader* s) { g_backends[g_active].destroy_shader(s); }
void rhi_destroy_pipeline(RhiPipeline* p) { g_backends[g_active].destroy_pipeline(p); }

int32_t rhi_buffer_upload(RhiBuffer* b, const void* data, uint64_t size) {
    return g_backends[g_active].buffer_upload(b, data, size);
}
int32_t rhi_texture_readback(RhiTexture* t, void* out, uint64_t out_size, uint32_t stride) {
    return g_backends[g_active].texture_readback(t, out, out_size, stride);
}

RhiCommandList* rhi_begin_cmdlist(RhiDevice* d) { return g_backends[g_active].begin_cmdlist(d); }
int32_t rhi_submit(RhiDevice* d, RhiCommandList* cl) { return g_backends[g_active].submit(d, cl); }
void rhi_cmd_pipeline_barrier(RhiCommandList* cl, uint32_t n, const RhiBarrier* b) {
    g_backends[g_active].cmd_pipeline_barrier(cl, n, b);
}

RhiEncoder* rhi_begin_render_pass(RhiCommandList* cl, const RhiPassDesc* desc) {
    return g_backends[g_active].begin_render_pass(cl, desc);
}
RhiEncoder* rhi_begin_compute_pass(RhiCommandList* cl, const char* name) {
    return g_backends[g_active].begin_compute_pass(cl, name);
}
void rhi_end_pass(RhiEncoder* enc) { g_backends[g_active].end_pass(enc); }

void rhi_cmd_bind_pipeline(RhiEncoder* e, RhiPipeline* p) { g_backends[g_active].cmd_bind_pipeline(e, p); }
void rhi_cmd_bind_vertex_buffer(RhiEncoder* e, uint32_t slot, RhiBuffer* b, uint64_t off) {
    g_backends[g_active].cmd_bind_vertex_buffer(e, slot, b, off);
}
void rhi_cmd_bind_uniform_buffer(RhiEncoder* e, uint32_t slot, RhiBuffer* b) {
    g_backends[g_active].cmd_bind_uniform_buffer(e, slot, b);
}
void rhi_cmd_set_viewport(RhiEncoder* e, float x, float y, float w, float h,
                          float min_depth, float max_depth) {
    g_backends[g_active].cmd_set_viewport(e, x, y, w, h, min_depth, max_depth);
}
void rhi_cmd_set_scissor(RhiEncoder* e, uint32_t x, uint32_t y, uint32_t w, uint32_t h) {
    g_backends[g_active].cmd_set_scissor(e, x, y, w, h);
}
void rhi_cmd_set_clear_color(RhiEncoder* e, float r, float g, float b, float a) {
    g_backends[g_active].cmd_set_clear_color(e, r, g, b, a);
}
void rhi_cmd_draw(RhiEncoder* e, const RhiDrawArgs* a) { g_backends[g_active].cmd_draw(e, a); }
void rhi_cmd_dispatch(RhiEncoder* e, uint32_t gx, uint32_t gy, uint32_t gz) {
    g_backends[g_active].cmd_dispatch(e, gx, gy, gz);
}

extern void rhi_metal_register(void);

/* macOS-only embed helpers. The implementations live in rhi_metal.mm; non-Apple
 * builds get the no-op forwarder below so the C ABI surface stays uniform
 * across platforms. */
#ifdef __APPLE__
extern void* metal_create_macos_metal_view(void* parent_view_handle,
                                            uint32_t width, uint32_t height);
extern void  metal_destroy_macos_metal_view(void* view_handle);
#endif

ENGINE_API void* rhi_create_macos_metal_view(void* parent_view_handle,
                                             uint32_t width, uint32_t height) {
#ifdef __APPLE__
    return metal_create_macos_metal_view(parent_view_handle, width, height);
#else
    (void)parent_view_handle;
    (void)width;
    (void)height;
    return NULL;
#endif
}

ENGINE_API void rhi_destroy_macos_metal_view(void* view_handle) {
#ifdef __APPLE__
    metal_destroy_macos_metal_view(view_handle);
#else
    (void)view_handle;
#endif
}

/* Lazy-register Metal on first rhi_init. The previous __attribute__((constructor))
 * that ran at dylib-load time crashed under Avalonia on macOS with
 * EXC_BAD_ACCESS(0x0) inside rhi_metal_register + 428 — Foundation /
 * AppKit frameworks are still mid-initialization at static-init time on
 * the Avalonia host, and the absolute pointer relocations the .mm TU emits
 * into rhi_metal_register's call sequence are not yet valid. Calling into
 * RHI from C# after AvaloniaMain is up is safe; constructors are not. */
static void rhi_ensure_default_backends(void) {
    if (g_backend_count > 0) return;   /* already registered (idempotent) */
#ifdef __APPLE__
    rhi_metal_register();
#endif
}
