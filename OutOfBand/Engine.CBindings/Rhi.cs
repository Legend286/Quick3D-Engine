// SPDX-License-Identifier: MIT
// P/Invoke surface for engine_c/rhi/rhi.h.
// AOT-friendly: every export uses [LibraryImport] source-generator.

using System;
using System.Runtime.InteropServices;

namespace Engine.CBindings;

public static partial class RhiNative
{
    public const string Library = "EngineC";

    // ---- enums (mirror C) ----

    public enum AccelStructType : uint
    {
        Blas = 0,
        Tlas = 1,
    }

    public enum VertexFormat : uint
    {
        Undefined = 0,
        Float3 = 1,
        Float2 = 2,
    }

    public enum TextureFormat
    {
        Undefined = 0,
        Rgba8Unorm = 1,
        Rgba8Srgb = 2,
        Rgba16Float = 3,
        Bgra8Unorm = 4,
        Depth32Float = 5,
        Depth24Stencil8 = 6,
        Bc1RgbUnormBlock = 20,
        Bc1RgbaUnormBlock = 21,
        Bc3UnormBlock = 22,
        Bc5UnormBlock = 23,
        Bc7UnormBlock = 24,
        Etc2Rgb8UnormBlock = 25,
        Astc4x4UnormBlock = 26,
    }

    [Flags]
    public enum BufferUsage : uint
    {
        None = 0,
        Vertex = 1u << 0,
        Index = 1u << 1,
        Uniform = 1u << 2,
        Storage = 1u << 3,
        Indirect = 1u << 4,
    }

    [Flags]
    public enum ShaderStage : uint
    {
        None = 0,
        Vertex = 1u << 0,
        Fragment = 1u << 1,
        Compute = 1u << 2,
    }

    public enum LoadOp { Load = 0, Clear = 1, Discard = 2 }
    public enum StoreOp { Store = 0, Discard = 1 }

    public enum ResourceState : uint
    {
        Undefined = 0,
        RenderTarget = 1,
        DepthWrite = 2,
        DepthRead = 3,
        ShaderRead = 4,
        UnorderedAccess = 5,
        CopySource = 6,
        CopyDest = 7,
        Present = 8
    }

    public enum QueueType : uint
    {
        Graphics = 0,
        Compute = 1,
    }

    public const uint TextureRenderTarget = 1u << 0;
    public const uint TextureShaderRead = 1u << 1;
    public const uint TextureCopySrc = 1u << 2;
    public const uint TextureCopyDst = 1u << 3;
    public const uint TextureStorage = 1u << 4;

    // ---- structs (mirror C layouts; abi first, then fields) ----

    [StructLayout(LayoutKind.Sequential)]
    public struct TextureDesc
    {
        public uint Abi;
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public TextureFormat Format;
        public uint UsageFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferDesc
    {
        public uint Abi;
        public ulong Size;
        public BufferUsage Usage;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ShaderDesc
    {
        public uint Abi;
        public ShaderStage Stages;
        public IntPtr Source;       // char*; UTF-8 NUL-terminated is not required,
                                    // length comes from SourceLen.
        public uint SourceLen;
        public IntPtr EntryPoint;   // char*; "main0" or similar.
        public IntPtr IncludePath;  // char*; optional base include path.
    }

    public enum PrimitiveTopology : uint
    {
        TriangleList = 0,
        LineList = 1,
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GraphicsPipelineDesc
    {
        public uint Abi;
        public IntPtr VertexShader;     // RhiShader*
        public IntPtr FragmentShader;
        public TextureFormat ColorFormat;
        public int EnableDepth;
        public int EnableBlend;
        public int SampleCount;
        public uint PrimitiveTopology;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ComputePipelineDesc
    {
        public uint Abi;
        public IntPtr ComputeShader;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HeapDesc
    {
        public uint Abi;
        public ulong Size;
        public uint UsageFlags;
    }

    public const uint HeapUsageRenderTarget = 1u << 0;
    public const uint HeapUsageShaderRead    = 1u << 1;
    public const uint HeapUsageStorage       = 1u << 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct PassAttachment
    {
        public IntPtr Texture;          // RhiTexture*
        public LoadOp LoadOp;
        public StoreOp StoreOp;
        public uint MipLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PassDesc
    {
        public uint Abi;
        public IntPtr ColorAttachments; // PassAttachment*
        public uint ColorCount;
        public IntPtr DepthAttachment;  // PassAttachment* (nullable)
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Barrier
    {
        public ulong Resource;          // RhiResourceHandle (u64)
        public uint _pad;
        public ResourceState StateBefore;
        public ResourceState StateAfter;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DrawArgs
    {
        public uint Abi;
        public uint VertexCount;
        public uint InstanceCount;
        public uint FirstVertex;
        public uint FirstInstance;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DrawIndexedArgs
    {
        public uint Abi;
        public uint IndexCount;
        public uint InstanceCount;
        public uint FirstIndex;
        public int VertexOffset;
        public uint FirstInstance;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DrawIndirectArgs
    {
        public uint Abi;
        public IntPtr IndirectBuffer;
        public ulong IndirectBufferOffset;
        public uint DrawCount;
        public uint Stride;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DrawIndexedIndirectArgs
    {
        public uint Abi;
        public IntPtr IndirectBuffer;
        public ulong IndirectBufferOffset;
        public uint DrawCount;
        public uint Stride;
    }

    // ---- device / swapchain / resources ----

    [LibraryImport(Library, EntryPoint = "rhi_init")]
    public static partial int RhiInit(out IntPtr outDevice);   // RhiDevice*

    [LibraryImport(Library, EntryPoint = "rhi_shutdown")]
    public static partial void RhiShutdown(IntPtr device);

    [LibraryImport(Library, EntryPoint = "rhi_create_swapchain")]
    public static partial int RhiCreateSwapchain(IntPtr device,
                                                  IntPtr osWindowHandle,
                                                  uint width, uint height,
                                                  out IntPtr outSwapchain);

    [LibraryImport(Library, EntryPoint = "rhi_destroy_swapchain")]
    public static partial void RhiDestroySwapchain(IntPtr sc);

    [LibraryImport(Library, EntryPoint = "rhi_acquire_next_image")]
    public static partial uint RhiAcquireNextImage(IntPtr sc,
                                                   out IntPtr outImage);

    [LibraryImport(Library, EntryPoint = "rhi_present")]
    public static partial int RhiPresent(IntPtr sc);

    [LibraryImport(Library, EntryPoint = "rhi_swapchain_get_size")]
    public static partial void RhiSwapchainGetSize(IntPtr sc,
                                                   out uint outWidth,
                                                   out uint outHeight);

    [LibraryImport(Library, EntryPoint = "rhi_create_buffer")]
    public static partial int RhiCreateBuffer(IntPtr device,
                                               in BufferDesc desc,
                                               out IntPtr outBuf);

    [LibraryImport(Library, EntryPoint = "rhi_create_texture")]
    public static partial int RhiCreateTexture(IntPtr device,
                                                in TextureDesc desc,
                                                out IntPtr outTex);

    [LibraryImport(Library, EntryPoint = "rhi_create_shader")]
    public static partial int RhiCreateShader(IntPtr device,
                                               in ShaderDesc desc,
                                               out IntPtr outShader);

    [LibraryImport(Library, EntryPoint = "rhi_create_graphics_pipeline")]
    public static partial int RhiCreateGraphicsPipeline(IntPtr device,
                                                        in GraphicsPipelineDesc desc,
                                                        out IntPtr outPipe);

    [LibraryImport(Library, EntryPoint = "rhi_create_compute_pipeline")]
    public static partial int RhiCreateComputePipeline(IntPtr device,
                                                       in ComputePipelineDesc desc,
                                                       out IntPtr outPipe);

    [LibraryImport(Library, EntryPoint = "rhi_destroy_buffer")]
    public static partial void RhiDestroyBuffer(IntPtr buf);

    [LibraryImport(Library, EntryPoint = "rhi_destroy_texture")]
    public static partial void RhiDestroyTexture(IntPtr tex);

    [LibraryImport(Library, EntryPoint = "rhi_create_sampler")]
    public static partial IntPtr RhiCreateSampler(IntPtr dev);

    [LibraryImport(Library, EntryPoint = "rhi_destroy_sampler")]
    public static partial void RhiDestroySampler(IntPtr samp);

    [LibraryImport(Library, EntryPoint = "rhi_destroy_shader")]
    public static partial void RhiDestroyShader(IntPtr sh);

    [LibraryImport(Library, EntryPoint = "rhi_destroy_pipeline")]
    public static partial void RhiDestroyPipeline(IntPtr p);

    [LibraryImport(Library, EntryPoint = "rhi_create_heap")]
    public static partial int RhiCreateHeap(IntPtr device, in HeapDesc desc, out IntPtr outHeap);

    [LibraryImport(Library, EntryPoint = "rhi_create_texture_from_heap")]
    public static partial int RhiCreateTextureFromHeap(IntPtr device, IntPtr heap, in TextureDesc desc, ulong offset, out IntPtr outTex);

    [LibraryImport(Library, EntryPoint = "rhi_create_buffer_from_heap")]
    public static partial int RhiCreateBufferFromHeap(IntPtr device, IntPtr heap, in BufferDesc desc, ulong offset, out IntPtr outBuf);

    [LibraryImport(Library, EntryPoint = "rhi_create_fence")]
    public static partial int RhiCreateFence(IntPtr device, out IntPtr outFence);

    [LibraryImport(Library, EntryPoint = "rhi_destroy_heap")]
    public static partial void RhiDestroyHeap(IntPtr h);
    
    [LibraryImport(Library, EntryPoint = "rhi_destroy_fence")]
    public static partial void RhiDestroyFence(IntPtr f);

    // ---- Bindless heap ----
    //
    // Additive ABI: legacy rhi_cmd_bind_texture_array keeps working. Bindless
    // heaps give shaders unbounded descriptor indexing. capacity=0 requests
    // the device's natural cap (Metal: MTLDevice.maxArgumentBufferSamplerCount,
    // falling back to 65536 on legacy OS).

    [StructLayout(LayoutKind.Sequential)]
    public struct BlasGeometryDesc
    {
        public IntPtr VertexBuffer;
        public ulong VertexBufferOffset;
        public uint VertexStride;
        public uint VertexCount;
        public VertexFormat VertexFormat;
        public IntPtr IndexBuffer;
        public ulong IndexBufferOffset;
        public uint IndexCount;
        public int Is32BitIndex;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct TlasInstanceDesc
    {
        public fixed float Transform[12];
        public uint InstanceId;
        public uint Mask;
        public uint InstanceOffset;
        public uint Flags;
        public IntPtr Blas;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AccelStructDesc
    {
        public uint Abi;
        public AccelStructType Type;
        
        // For BLAS
        public IntPtr Geometries; // RhiBlasGeometryDesc*
        public uint GeometryCount;
        
        // For TLAS
        public IntPtr Instances; // RhiTlasInstanceDesc*
        public uint InstanceCount;
    }

    [LibraryImport(Library, EntryPoint = "rhi_create_accel_struct")]
    public static partial int RhiCreateAccelStruct(IntPtr device, in AccelStructDesc desc, out IntPtr outAs);

    [LibraryImport(Library, EntryPoint = "rhi_destroy_accel_struct")]
    public static partial void RhiDestroyAccelStruct(IntPtr accelStruct);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_build_accel_structs")]
    public static partial void RhiCmdBuildAccelStructs(IntPtr cmdList, IntPtr accelStructsArray, uint count);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_compact_accel_structs")]
    public static partial void RhiCmdCompactAccelStructs(IntPtr cmdList, IntPtr accelStructsArray, uint count);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_bind_accel_struct")]
    public static partial void RhiCmdBindAccelStruct(IntPtr encoder, uint slot, IntPtr accelStruct);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_use_accel_struct")]
    public static partial void RhiCmdUseAccelStruct(IntPtr encoder, IntPtr accelStruct, uint usage);

    [StructLayout(LayoutKind.Sequential)]
    public struct BindlessHeapDesc
    {
        public uint Abi;
        public uint Capacity;
    }

    [LibraryImport(Library, EntryPoint = "rhi_create_bindless_heap")]
    public static partial int RhiCreateBindlessHeap(IntPtr device,
                                                    in BindlessHeapDesc desc,
                                                    out IntPtr outHeap);

    [LibraryImport(Library, EntryPoint = "rhi_destroy_bindless_heap")]
    public static partial void RhiDestroyBindlessHeap(IntPtr heap);

    [LibraryImport(Library, EntryPoint = "rhi_bindless_register_texture")]
    public static partial int RhiBindlessRegisterTexture(IntPtr heap,
                                                          IntPtr texture,
                                                          out uint outSlot);

    [LibraryImport(Library, EntryPoint = "rhi_bindless_release_texture")]
    public static partial void RhiBindlessReleaseTexture(IntPtr heap, uint slot);

    [LibraryImport(Library, EntryPoint = "rhi_bindless_lookup_slot")]
    public static partial int RhiBindlessLookupSlot(IntPtr heap,
                                                     IntPtr texture,
                                                     out uint outSlot);

    [LibraryImport(Library, EntryPoint = "rhi_get_buffer_device_address")]
    public static partial ulong RhiGetBufferDeviceAddress(IntPtr buf);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_signal_fence")]
    public static partial void RhiCmdSignalFence(IntPtr cmdlist, IntPtr fence, ulong value);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_wait_fence")]
    public static partial void RhiCmdWaitFence(IntPtr cmdlist, IntPtr fence, ulong value);

    // ---- macOS embed helpers ----
    //
    // rhi_create_macos_metal_view / rhi_destroy_macos_metal_view allocate +
    // destroy a child NSView suitable for hosting a CAMetalLayer sublayer
    // inside an Avalonia NativeControlHost. P/Invoke surface mirrors the
    // ENGINE_API C ABI; see engine_c/rhi/rhi.h.

    [LibraryImport(Library, EntryPoint = "rhi_create_macos_metal_view")]
    public static partial IntPtr RhiCreateMacosMetalView(IntPtr parentViewHandle,
                                                          uint width, uint height);

    [LibraryImport(Library, EntryPoint = "rhi_destroy_macos_metal_view")]
    public static partial void RhiDestroyMacosMetalView(IntPtr viewHandle);

    [LibraryImport(Library, EntryPoint = "rhi_buffer_upload")]
    public static partial int RhiBufferUpload(IntPtr buf,
                                               IntPtr data,
                                               ulong size);

    [LibraryImport(Library, EntryPoint = "rhi_texture_readback")]
    public static partial int RhiTextureReadback(IntPtr tex,
                                                 IntPtr outBytes,
                                                 ulong outSize,
                                                 uint outStride);

    [LibraryImport(Library, EntryPoint = "rhi_texture_upload")]
    public static partial int RhiTextureUpload(IntPtr tex,
                                               IntPtr bytes,
                                               ulong size,
                                               uint stride);

    [LibraryImport(Library, EntryPoint = "rhi_texture_upload_mip")]
    public static partial int RhiTextureUploadMip(IntPtr tex,
                                                   uint mipLevel,
                                                   IntPtr bytes,
                                                   ulong size,
                                                   uint stride);

    [LibraryImport(Library, EntryPoint = "rhi_format_block_info")]
    public static partial void RhiFormatBlockInfo(TextureFormat fmt,
                                                   out uint outBlockW,
                                                   out uint outBlockH,
                                                   out uint outBytesPerBlock);

    // ---- command-list / encoders ----

    [LibraryImport(Library, EntryPoint = "rhi_begin_cmdlist")]
    public static partial IntPtr RhiBeginCmdlist(IntPtr device, QueueType queue);

    [LibraryImport(Library, EntryPoint = "rhi_submit")]
    public static partial int RhiSubmit(IntPtr device, IntPtr cmdlist);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_pipeline_barrier")]
    public static partial void RhiCmdPipelineBarrier(IntPtr cmdlist,
                                                      uint count,
                                                      IntPtr barriers);

    [LibraryImport(Library, EntryPoint = "rhi_begin_render_pass")]
    public static partial IntPtr RhiBeginRenderPass(IntPtr cmdlist,
                                                    in PassDesc desc);

    [LibraryImport(Library, EntryPoint = "rhi_begin_compute_pass")]
    public static partial IntPtr RhiBeginComputePass(IntPtr cmdlist,
                                                     IntPtr debugName);

    [LibraryImport(Library, EntryPoint = "rhi_end_pass")]
    public static partial void RhiEndPass(IntPtr encoder);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_bind_pipeline")]
    public static partial void RhiCmdBindPipeline(IntPtr encoder, IntPtr pipeline);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_bind_vertex_buffer")]
    public static partial void RhiCmdBindVertexBuffer(IntPtr encoder,
                                                       uint slot, IntPtr buf,
                                                       ulong offset);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_bind_uniform_buffer")]
    public static partial void RhiCmdBindUniformBuffer(IntPtr encoder,
                                                        uint slot, IntPtr buf);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_set_viewport")]
    public static partial void RhiCmdSetViewport(IntPtr encoder,
                                                  float x, float y,
                                                  float w, float h,
                                                  float minDepth, float maxDepth);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_set_scissor")]
    public static partial void RhiCmdSetScissor(IntPtr encoder,
                                                 uint x, uint y,
                                                 uint w, uint h);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_set_clear_color")]
    public static partial void RhiCmdSetClearColor(IntPtr encoder,
                                                    float r, float g,
                                                    float b, float a);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_push_constants")]
    public static partial void RhiCmdPushConstants(IntPtr encoder,
                                                   uint slot,
                                                   uint size, IntPtr data);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_draw")]
    public static partial void RhiCmdDraw(IntPtr encoder, in DrawArgs args);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_draw_indirect")]
    public static partial void RhiCmdDrawIndirect(IntPtr encoder, in DrawIndirectArgs args);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_draw_indexed")]
    public static partial void RhiCmdDrawIndexed(IntPtr encoder, in DrawIndexedArgs args);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_draw_indexed_indirect")]
    public static partial void RhiCmdDrawIndexedIndirect(IntPtr encoder, in DrawIndexedIndirectArgs args);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_bind_index_buffer")]
    public static partial void RhiCmdBindIndexBuffer(IntPtr encoder, IntPtr buf, int is32Bit, ulong offset);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_bind_texture")]
    public static partial void RhiCmdBindTexture(IntPtr encoder, uint slot, IntPtr tex);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_bind_texture_array")]
    public static partial void RhiCmdBindTextureArray(IntPtr encoder, uint slot, ref IntPtr texs, uint count);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_bind_bindless_heap")]
    public static partial void RhiCmdBindBindlessHeap(IntPtr encoder, IntPtr heap, uint slot);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_bind_sampler")]
    public static partial void RhiCmdBindSampler(nint encoder, uint slot, nint samp);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_use_buffer")]
    public static partial void RhiCmdUseBuffer(nint encoder, nint buf, uint usage);

    [LibraryImport(Library, EntryPoint = "rhi_cmd_dispatch")]
    public static partial void RhiCmdDispatch(IntPtr encoder,
                                               uint gx, uint gy, uint gz,
                                               uint tg_x, uint tg_y, uint tg_z);
}
