// SPDX-License-Identifier: MIT
// Metal RHI backend (macOS / iOS Apple Silicon).
//
// Implements the engine_c/rhi/rhi.h vtable for Metal. rhi_dispatch.c calls
// rhi_metal_register() at static-init time which installs this backend.
//
// Source language: Objective-C++ so that Metal Cocoa headers (MTL*.h,
// CAMetalLayer, NSWindow) can be used directly. The C/C++ surface stays
// in rhi_dispatch.c; this file is the obj-c implementation behind that.
//
// Lifetime: every Rhi* handle returned to the dispatcher is a heap-allocated
// Rhi*Impl struct that owns its Metal counterparts via ARC attribute on the
// ivars. Caller frees via rhi_destroy_* which calls free() and lets ARC
// release the underlying objects. Texture impls are allocated per
// acquire_next_image - the consumer (executor / readback) is the owner.

#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#import <MetalKit/MetalKit.h>
#import <QuartzCore/CAMetalLayer.h>
#import <AppKit/AppKit.h>

#include "rhi.h"
#include "rhi_backend.h"
#include "engine_log.h"

#include <cstring>

namespace {

// Backing impl structs. ARC '__strong' so the Objective-C objects vanish when
// the impl is freed.

struct RhiDeviceImpl {
    __strong id<MTLDevice>        device;
    __strong id<MTLCommandQueue>  queue;
};

struct RhiSwapchainImpl {
    __strong CAMetalLayer*        layer;
    __strong id<CAMetalDrawable>  drawable;     /* current image */
    __strong id<MTLTexture>       color_image;  /* = drawable.texture shortcut */
    uint32_t                      width;
    uint32_t                      height;
};

struct RhiBufferImpl {
    __strong id<MTLBuffer> buf;
};

struct RhiTextureImpl {
    __strong id<MTLTexture> tex;
};

struct RhiShaderImpl {
    __strong id<MTLLibrary> lib;
    __strong id<MTLFunction> fn;
};

struct RhiPipelineImpl {
    __strong id<MTLRenderPipelineState> g;
    __strong id<MTLComputePipelineState> c;
};

struct RhiCommandListImpl {
    __strong id<MTLCommandBuffer> buf;
};

struct RhiEncoderImpl {
    __strong id<MTLRenderCommandEncoder>  render;
    __strong id<MTLComputeCommandEncoder> compute;
    bool is_compute;
};

// ----- trampolines (no overloads; all explicit argument lists) -----

static int32_t  metal_init(RhiDevice** out_device);
static void     metal_shutdown(RhiDevice* device);
static int32_t  metal_create_swapchain(RhiDevice* d, void* os_win,
                                        uint32_t w, uint32_t h,
                                        RhiSwapchain** out_sc);
static void     metal_destroy_swapchain(RhiSwapchain* sc);
static uint32_t metal_acquire_next_image(RhiSwapchain* sc, RhiTexture** out_image);
static int32_t  metal_present(RhiSwapchain* sc);

static int32_t  metal_create_buffer(RhiDevice* d, const RhiBufferDesc* desc, RhiBuffer** out);
static int32_t  metal_create_texture(RhiDevice* d, const RhiTextureDesc* desc, RhiTexture** out);
static int32_t  metal_create_shader(RhiDevice* d, const RhiShaderDesc* desc, RhiShader** out);
static int32_t  metal_create_graphics_pipeline(RhiDevice* d,
                                                const RhiGraphicsPipelineDesc* desc,
                                                RhiPipeline** out);
static int32_t  metal_create_compute_pipeline(RhiDevice* d,
                                               const RhiComputePipelineDesc* desc,
                                               RhiPipeline** out);
static void  metal_destroy_buffer(RhiBuffer* b);
static void  metal_destroy_texture(RhiTexture* t);
static void  metal_destroy_shader(RhiShader* s);
static void  metal_destroy_pipeline(RhiPipeline* p);
static int32_t  metal_buffer_upload(RhiBuffer* b, const void* data, uint64_t size);
static int32_t  metal_texture_readback(RhiTexture* t, void* out, uint64_t out_size, uint32_t stride);

static RhiCommandList* metal_begin_cmdlist(RhiDevice* d);
static int32_t         metal_submit(RhiDevice* d, RhiCommandList* cl);
static void            metal_cmd_pipeline_barrier(RhiCommandList* cl,
                                                   uint32_t count,
                                                   const RhiBarrier* barriers);
static RhiEncoder*     metal_begin_render_pass(RhiCommandList* cl,
                                                const RhiPassDesc* desc);
static RhiEncoder*     metal_begin_compute_pass(RhiCommandList* cl,
                                                 const char* debug_name);
static void            metal_end_pass(RhiEncoder* enc);

static void metal_cmd_bind_pipeline(RhiEncoder* e, RhiPipeline* p);
static void metal_cmd_bind_vertex_buffer(RhiEncoder* e, uint32_t slot,
                                          RhiBuffer* b, uint64_t offset);
static void metal_cmd_bind_uniform_buffer(RhiEncoder* e, uint32_t slot,
                                           RhiBuffer* b);
static void metal_cmd_set_viewport(RhiEncoder* e, float x, float y,
                                    float w, float h,
                                    float min_depth, float max_depth);
static void metal_cmd_set_scissor(RhiEncoder* e, uint32_t x, uint32_t y,
                                   uint32_t w, uint32_t h);
static void metal_cmd_set_clear_color(RhiEncoder* e, float r, float g,
                                       float b, float a);
static void metal_cmd_draw(RhiEncoder* e, const RhiDrawArgs* a);
static void metal_cmd_dispatch(RhiEncoder* e, uint32_t gx, uint32_t gy, uint32_t gz);

// ----- impl -----

static int32_t metal_init(RhiDevice** out_device) {
    @autoreleasepool {
        id<MTLDevice> dev = MTLCreateSystemDefaultDevice();
        if (!dev) {
            ENGINE_LOG_ERROR("rhi_metal", "MTLCreateSystemDefaultDevice returned nil");
            return -1;
        }
        id<MTLCommandQueue> queue = [dev newCommandQueue];
        if (!queue) return -2;
        RhiDeviceImpl* di = new RhiDeviceImpl();
        di->device = dev;
        di->queue  = queue;
        *out_device = reinterpret_cast<RhiDevice*>(di);
        ENGINE_LOG_INFO("rhi_metal", "device=%s queue ready",
                        [[dev name] UTF8String]);
        return 0;
    }
}

static void metal_shutdown(RhiDevice* device) {
    if (!device) return;
    RhiDeviceImpl* di = reinterpret_cast<RhiDeviceImpl*>(device);
    delete di;  // ARC zeroes the ivars -> releases device and queue
}

static int32_t metal_create_swapchain(RhiDevice* d, void* os_win_handle,
                                       uint32_t w, uint32_t h, RhiSwapchain** out) {
    @autoreleasepool {
        RhiDeviceImpl* di = reinterpret_cast<RhiDeviceImpl*>(d);
        NSWindow* win = (__bridge NSWindow*)os_win_handle;
        if (!win) {
            ENGINE_LOG_ERROR("rhi_metal", "swapchain needs an NSWindow*");
            return -1;
        }
        CAMetalLayer* layer = [CAMetalLayer layer];
        layer.device      = di->device;
        layer.drawableSize = CGSizeMake((CGFloat)w, (CGFloat)h);
        layer.pixelFormat = MTLPixelFormatBGRA8Unorm;
        layer.framebufferOnly = YES;  // hi-perf path
        win.contentView.wantsLayer = YES;
        win.contentView.layer      = layer;
        RhiSwapchainImpl* sc = new RhiSwapchainImpl();
        sc->layer       = layer;
        sc->drawable    = nil;
        sc->color_image = nil;
        sc->width       = w;
        sc->height      = h;
        *out = reinterpret_cast<RhiSwapchain*>(sc);
        return 0;
    }
}

static void metal_destroy_swapchain(RhiSwapchain* p) {
    if (!p) return;
    RhiSwapchainImpl* sc = reinterpret_cast<RhiSwapchainImpl*>(p);
    delete sc;
}

static uint32_t metal_acquire_next_image(RhiSwapchain* p, RhiTexture** out_image) {
    @autoreleasepool {
        RhiSwapchainImpl* sc = reinterpret_cast<RhiSwapchainImpl*>(p);
        id<CAMetalDrawable> drawable = [sc->layer nextDrawable];
        if (!drawable) {
            ENGINE_LOG_ERROR("rhi_metal", "nextDrawable nil (window hidden?)");
            return 0;
        }
        sc->drawable    = drawable;
        sc->color_image = drawable.texture;
        // Allocate a fresh texture impl per acquire. Consumer (executor or
        // readback caller) takes ownership and disposes when done.
        RhiTextureImpl* ti = new RhiTextureImpl();
        ti->tex = drawable.texture;
        *out_image = reinterpret_cast<RhiTexture*>(ti);
        return 1;
    }
}

static int32_t metal_present(RhiSwapchain* p) {
    if (!p) return -1;
    RhiSwapchainImpl* sc = reinterpret_cast<RhiSwapchainImpl*>(p);
    if (!sc->drawable) return -2;
    // The drawable is presented when its command buffer (created via
    // rhi_begin_cmdlist with texture) commits. markPresented is implicit.
    return 0;
}

static int32_t metal_create_buffer(RhiDevice* d, const RhiBufferDesc* desc, RhiBuffer** out) {
    @autoreleasepool {
        RhiDeviceImpl* di = reinterpret_cast<RhiDeviceImpl*>(d);
        id<MTLBuffer> buf = [di->device newBufferWithLength:desc->size
                                                    options:MTLResourceStorageModeShared];
        if (!buf) return -1;
        RhiBufferImpl* bi = new RhiBufferImpl();
        bi->buf = buf;
        *out = reinterpret_cast<RhiBuffer*>(bi);
        return 0;
    }
}

static int32_t metal_create_texture(RhiDevice* d, const RhiTextureDesc* desc, RhiTexture** out) {
    @autoreleasepool {
        RhiDeviceImpl* di = reinterpret_cast<RhiDeviceImpl*>(d);
        MTLPixelFormat fmt = MTLPixelFormatBGRA8Unorm;
        switch (desc->format) {
            case RHI_FORMAT_RGBA8_UNORM:        fmt = MTLPixelFormatRGBA8Unorm; break;
            case RHI_FORMAT_RGBA8_SRGB:         fmt = MTLPixelFormatRGBA8Unorm_sRGB; break;
            case RHI_FORMAT_RGBA16_FLOAT:       fmt = MTLPixelFormatRGBA16Float; break;
            case RHI_FORMAT_BGRA8_UNORM:        fmt = MTLPixelFormatBGRA8Unorm; break;
            case RHI_FORMAT_DEPTH32_FLOAT:      fmt = MTLPixelFormatDepth32Float; break;
            case RHI_FORMAT_DEPTH24_STENCIL8:   fmt = MTLPixelFormatDepth24Unorm_Stencil8; break;
            default: break;
        }
        NSUInteger mip_count = desc->mip_levels > 0 ? desc->mip_levels : 1;
        MTLTextureDescriptor* td =
            [MTLTextureDescriptor texture2DDescriptorWithPixelFormat:fmt
                                                              width:desc->width
                                                             height:desc->height
                                                          mipmapped:(mip_count > 1)];
        td.mipmapLevelCount = mip_count;
        td.usage = MTLTextureUsageRenderTarget | MTLTextureUsageShaderRead;
        td.storageMode = MTLStorageModePrivate;
        id<MTLTexture> tex = [di->device newTextureWithDescriptor:td];
        if (!tex) return -1;
        RhiTextureImpl* ti = new RhiTextureImpl();
        ti->tex = tex;
        *out = reinterpret_cast<RhiTexture*>(ti);
        return 0;
    }
}

static int32_t metal_create_shader(RhiDevice* d, const RhiShaderDesc* desc, RhiShader** out) {
    @autoreleasepool {
        RhiDeviceImpl* di = reinterpret_cast<RhiDeviceImpl*>(d);
        NSString* src = [[NSString alloc] initWithBytesNoCopy:(void*)desc->source
                                                       length:desc->source_len
                                                     encoding:NSUTF8StringEncoding
                                                 freeWhenDone:NO];
        NSError* err = nil;
        id<MTLLibrary> lib = [di->device newLibraryWithSource:src options:nil error:&err];
        if (!lib) {
            ENGINE_LOG_ERROR("rhi_metal", "shader compile: %s",
                              [[err localizedDescription] UTF8String]);
            return -1;
        }
        NSString* entry = [NSString stringWithUTF8String:desc->entry_point];
        id<MTLFunction> fn = [lib newFunctionWithName:entry];
        if (!fn) {
            ENGINE_LOG_ERROR("rhi_metal", "entry point '%s' not found", desc->entry_point);
            return -2;
        }
        RhiShaderImpl* si = new RhiShaderImpl();
        si->lib = lib;
        si->fn  = fn;
        *out = reinterpret_cast<RhiShader*>(si);
        return 0;
    }
}

static int32_t metal_create_graphics_pipeline(RhiDevice* d,
                                              const RhiGraphicsPipelineDesc* desc,
                                              RhiPipeline** out) {
    @autoreleasepool {
        RhiDeviceImpl* di = reinterpret_cast<RhiDeviceImpl*>(d);
        RhiShaderImpl* vs = reinterpret_cast<RhiShaderImpl*>(desc->vertex_shader);
        RhiShaderImpl* fs = reinterpret_cast<RhiShaderImpl*>(desc->fragment_shader);
        MTLRenderPipelineDescriptor* pd = [MTLRenderPipelineDescriptor new];
        pd.vertexFunction   = vs->fn;
        pd.fragmentFunction = fs->fn;
        MTLPixelFormat color = MTLPixelFormatBGRA8Unorm;
        switch (desc->color_attachment_format) {
            case RHI_FORMAT_RGBA8_UNORM:  color = MTLPixelFormatRGBA8Unorm; break;
            case RHI_FORMAT_BGRA8_UNORM:  color = MTLPixelFormatBGRA8Unorm; break;
            case RHI_FORMAT_RGBA8_SRGB:   color = MTLPixelFormatRGBA8Unorm_sRGB; break;
            default: break;
        }
        pd.colorAttachments[0].pixelFormat      = color;
        pd.depthAttachmentPixelFormat = desc->enable_depth
            ? MTLPixelFormatDepth32Float
            : MTLPixelFormatInvalid;
        NSError* err = nil;
        id<MTLRenderPipelineState> state =
            [di->device newRenderPipelineStateWithDescriptor:pd error:&err];
        if (!state) {
            ENGINE_LOG_ERROR("rhi_metal", "pipeline: %s",
                              [[err localizedDescription] UTF8String]);
            return -1;
        }
        RhiPipelineImpl* pi = new RhiPipelineImpl();
        pi->g = state;
        pi->c = nil;
        *out = reinterpret_cast<RhiPipeline*>(pi);
        return 0;
    }
}

static int32_t metal_create_compute_pipeline(RhiDevice* d,
                                             const RhiComputePipelineDesc* desc,
                                             RhiPipeline** out) {
    @autoreleasepool {
        RhiDeviceImpl* di = reinterpret_cast<RhiDeviceImpl*>(d);
        RhiShaderImpl* cs = reinterpret_cast<RhiShaderImpl*>(desc->compute_shader);
        NSError* err = nil;
        id<MTLComputePipelineState> state =
            [di->device newComputePipelineStateWithFunction:cs->fn error:&err];
        if (!state) {
            ENGINE_LOG_ERROR("rhi_metal", "compute pipeline: %s",
                              [[err localizedDescription] UTF8String]);
            return -1;
        }
        RhiPipelineImpl* pi = new RhiPipelineImpl();
        pi->g = nil;
        pi->c = state;
        *out = reinterpret_cast<RhiPipeline*>(pi);
        return 0;
    }
}

static void metal_destroy_buffer(RhiBuffer* p) {
    if (!p) return;
    delete reinterpret_cast<RhiBufferImpl*>(p);
}
static void metal_destroy_texture(RhiTexture* p) {
    if (!p) return;
    delete reinterpret_cast<RhiTextureImpl*>(p);
}
static void metal_destroy_shader(RhiShader* p) {
    if (!p) return;
    delete reinterpret_cast<RhiShaderImpl*>(p);
}
static void metal_destroy_pipeline(RhiPipeline* p) {
    if (!p) return;
    delete reinterpret_cast<RhiPipelineImpl*>(p);
}

static int32_t metal_buffer_upload(RhiBuffer* b, const void* data, uint64_t size) {
    RhiBufferImpl* bi = reinterpret_cast<RhiBufferImpl*>(b);
    if (size > (uint64_t)[bi->buf length]) {
        ENGINE_LOG_ERROR("rhi_metal", "upload exceeds buffer size (%llu > %llu)",
                         (unsigned long long)size,
                         (unsigned long long)[bi->buf length]);
        return -1;
    }
    memcpy([bi->buf contents], data, (size_t)size);
    return 0;
}

static int32_t metal_texture_readback(RhiTexture* t, void* out,
                                        uint64_t out_size, uint32_t stride) {
    @autoreleasepool {
        RhiTextureImpl* ti = reinterpret_cast<RhiTextureImpl*>(t);
        NSUInteger h = ti->tex.height;
        NSUInteger w = ti->tex.width;
        if ((uint64_t)h * stride > out_size) {
            ENGINE_LOG_ERROR("rhi_metal", "readback buffer too small");
            return -1;
        }
        MTLRegion region = MTLRegionMake2D(0, 0, w, h);
        [ti->tex getBytes:out bytesPerRow:stride fromRegion:region mipmapLevel:0];
        return 0;
    }
}

static RhiCommandList* metal_begin_cmdlist(RhiDevice* d) {
    @autoreleasepool {
        RhiDeviceImpl* di = reinterpret_cast<RhiDeviceImpl*>(d);
        id<MTLCommandBuffer> cb = [di->queue commandBuffer];
        if (!cb) return nullptr;
        RhiCommandListImpl* cl = new RhiCommandListImpl();
        cl->buf = cb;
        return reinterpret_cast<RhiCommandList*>(cl);
    }
}

static int32_t metal_submit(RhiDevice* d, RhiCommandList* cl) {
    @autoreleasepool {
        RhiCommandListImpl* cli = reinterpret_cast<RhiCommandListImpl*>(cl);
        [cli->buf commit];
        return 0;
    }
}

static void metal_cmd_pipeline_barrier(RhiCommandList*, uint32_t, const RhiBarrier*) {
    /* Metal tracks dependencies implicitly via encoder ordering. The ABI
       signature is preserved for forward Vulkan compatibility. */
}

static RhiEncoder* metal_begin_render_pass(RhiCommandList* cl, const RhiPassDesc* desc) {
    @autoreleasepool {
        RhiCommandListImpl* cli = reinterpret_cast<RhiCommandListImpl*>(cl);
        MTLRenderPassDescriptor* pd = [MTLRenderPassDescriptor new];
        for (uint32_t i = 0; i < desc->color_count; ++i) {
            RhiTextureImpl* ti = reinterpret_cast<RhiTextureImpl*>(desc->color_attachments[i].texture);
            pd.colorAttachments[i].texture = ti->tex;
            switch (desc->color_attachments[i].load_op) {
                case RHI_LOAD_OP_CLEAR:    pd.colorAttachments[i].loadAction = MTLLoadActionClear; break;
                case RHI_LOAD_OP_DISCARD:  pd.colorAttachments[i].loadAction = MTLLoadActionDontCare; break;
                default:                  pd.colorAttachments[i].loadAction = MTLLoadActionLoad; break;
            }
            switch (desc->color_attachments[i].store_op) {
                case RHI_STORE_OP_DISCARD: pd.colorAttachments[i].storeAction = MTLStoreActionDontCare; break;
                default:                  pd.colorAttachments[i].storeAction = MTLStoreActionStore; break;
            }
            pd.colorAttachments[i].clearColor = MTLClearColorMake(0.05, 0.06, 0.09, 1.0);
        }
        if (desc->depth_attachment) {
            RhiTextureImpl* dti = reinterpret_cast<RhiTextureImpl*>(desc->depth_attachment->texture);
            pd.depthAttachment.texture      = dti->tex;
            pd.depthAttachment.loadAction   = MTLLoadActionClear;
            pd.depthAttachment.storeAction  = MTLStoreActionStore;
            pd.depthAttachment.clearDepth   = 1.0;
        }
        id<MTLRenderCommandEncoder> enc = [cli->buf renderCommandEncoderWithDescriptor:pd];
        RhiEncoderImpl* ri = new RhiEncoderImpl();
        ri->render  = enc;
        ri->compute = nil;
        ri->is_compute = false;
        return reinterpret_cast<RhiEncoder*>(ri);
    }
}

static RhiEncoder* metal_begin_compute_pass(RhiCommandList* cl, const char* name) {
    @autoreleasepool {
        RhiCommandListImpl* cli = reinterpret_cast<RhiCommandListImpl*>(cl);
        id<MTLComputeCommandEncoder> enc = [cli->buf computeCommandEncoder];
        if (name) [enc setLabel:[NSString stringWithUTF8String:name]];
        RhiEncoderImpl* ri = new RhiEncoderImpl();
        ri->render  = nil;
        ri->compute = enc;
        ri->is_compute = true;
        return reinterpret_cast<RhiEncoder*>(ri);
    }
}

static void metal_end_pass(RhiEncoder* enc) {
    @autoreleasepool {
        RhiEncoderImpl* ri = reinterpret_cast<RhiEncoderImpl*>(enc);
        [ri->render  endEncoding];
        [ri->compute endEncoding];
        delete ri;
    }
}

static void metal_cmd_bind_pipeline(RhiEncoder* enc, RhiPipeline* p) {
    RhiEncoderImpl* ri = reinterpret_cast<RhiEncoderImpl*>(enc);
    RhiPipelineImpl* pi = reinterpret_cast<RhiPipelineImpl*>(p);
    if (ri->render && pi->g)  [ri->render  setRenderPipelineState:pi->g];
    if (ri->compute && pi->c) [ri->compute setComputePipelineState:pi->c];
}

static void metal_cmd_bind_vertex_buffer(RhiEncoder* enc, uint32_t slot,
                                          RhiBuffer* b, uint64_t off) {
    RhiEncoderImpl* ri = reinterpret_cast<RhiEncoderImpl*>(enc);
    if (!ri->render) return;
    RhiBufferImpl* bi = reinterpret_cast<RhiBufferImpl*>(b);
    [ri->render setVertexBuffer:bi->buf offset:off atIndex:slot];
}

static void metal_cmd_bind_uniform_buffer(RhiEncoder* enc, uint32_t slot, RhiBuffer* b) {
    RhiEncoderImpl* ri = reinterpret_cast<RhiEncoderImpl*>(enc);
    RhiBufferImpl* bi = reinterpret_cast<RhiBufferImpl*>(b);
    if (ri->render)  [ri->render  setVertexBuffer:bi->buf offset:0 atIndex:slot];
    if (ri->compute) [ri->compute setBuffer:bi->buf offset:0 atIndex:slot];
}

static void metal_cmd_set_viewport(RhiEncoder* enc, float x, float y, float w, float h,
                                    float min_depth, float max_depth) {
    RhiEncoderImpl* ri = reinterpret_cast<RhiEncoderImpl*>(enc);
    if (!ri->render) return;
    MTLViewport vp;
    vp.originX = x; vp.originY = y; vp.width = w; vp.height = h;
    vp.znear = min_depth; vp.zfar = max_depth;
    [ri->render setViewport:vp];
}

static void metal_cmd_set_scissor(RhiEncoder* enc, uint32_t x, uint32_t y,
                                   uint32_t w, uint32_t h) {
    RhiEncoderImpl* ri = reinterpret_cast<RhiEncoderImpl*>(enc);
    if (!ri->render) return;
    MTLScissorRect r;
    r.x = x; r.y = y; r.width = w; r.height = h;
    [ri->render setScissorRect:r];
}

static void metal_cmd_set_clear_color(RhiEncoder*, float, float, float, float) {
    /* Clear color is set on MTLRenderPassDescriptor at beginRenderPass. */
}

static void metal_cmd_draw(RhiEncoder* enc, const RhiDrawArgs* a) {
    RhiEncoderImpl* ri = reinterpret_cast<RhiEncoderImpl*>(enc);
    if (!ri->render) return;
    [ri->render drawPrimitives:MTLPrimitiveTypeTriangle
                    vertexStart:a->first_vertex
                    vertexCount:a->vertex_count
                  instanceCount:a->instance_count
                  baseInstance:a->first_instance];
}

static void metal_cmd_dispatch(RhiEncoder* enc, uint32_t gx, uint32_t gy, uint32_t gz) {
    RhiEncoderImpl* ri = reinterpret_cast<RhiEncoderImpl*>(enc);
    if (!ri->compute) return;
    [ri->compute dispatchThreadgroups:MTLSizeMake(gx, gy, gz)
                threadsPerThreadgroup:MTLSizeMake(8, 8, 1)];
}

}  // anonymous namespace

extern "C" void rhi_metal_register(void) {
    RhiBackend b = {};
    b.name = "metal";
    b.init                       = metal_init;
    b.shutdown                   = metal_shutdown;
    b.create_swapchain           = metal_create_swapchain;
    b.destroy_swapchain          = metal_destroy_swapchain;
    b.acquire_next_image         = metal_acquire_next_image;
    b.present                    = metal_present;
    b.create_buffer              = metal_create_buffer;
    b.create_texture             = metal_create_texture;
    b.create_shader              = metal_create_shader;
    b.create_graphics_pipeline   = metal_create_graphics_pipeline;
    b.create_compute_pipeline    = metal_create_compute_pipeline;
    b.destroy_buffer             = metal_destroy_buffer;
    b.destroy_texture            = metal_destroy_texture;
    b.destroy_shader             = metal_destroy_shader;
    b.destroy_pipeline           = metal_destroy_pipeline;
    b.buffer_upload              = metal_buffer_upload;
    b.texture_readback           = metal_texture_readback;
    b.begin_cmdlist              = metal_begin_cmdlist;
    b.submit                     = metal_submit;
    b.cmd_pipeline_barrier       = metal_cmd_pipeline_barrier;
    b.begin_render_pass          = metal_begin_render_pass;
    b.begin_compute_pass         = metal_begin_compute_pass;
    b.end_pass                   = metal_end_pass;
    b.cmd_bind_pipeline          = metal_cmd_bind_pipeline;
    b.cmd_bind_vertex_buffer     = metal_cmd_bind_vertex_buffer;
    b.cmd_bind_uniform_buffer    = metal_cmd_bind_uniform_buffer;
    b.cmd_set_viewport           = metal_cmd_set_viewport;
    b.cmd_set_scissor            = metal_cmd_set_scissor;
    b.cmd_set_clear_color        = metal_cmd_set_clear_color;
    b.cmd_draw                   = metal_cmd_draw;
    b.cmd_dispatch               = metal_cmd_dispatch;
    rhi_backend_register(&b);
}
