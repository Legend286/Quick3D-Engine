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

    public enum TextureFormat
    {
        Undefined        = 0,
        Rgba8Unorm       = 1,
        Rgba8Srgb        = 2,
        Rgba16Float      = 3,
        Bgra8Unorm       = 4,
        Depth32Float     = 5,
        Depth24Stencil8  = 6,
    }

    [Flags]
    public enum BufferUsage : uint
    {
        None    = 0,
        Vertex  = 1u << 0,
        Index   = 1u << 1,
        Uniform = 1u << 2,
        Storage = 1u << 3,
    }

    [Flags]
    public enum ShaderStage : uint
    {
        None     = 0,
        Vertex   = 1u << 0,
        Fragment = 1u << 1,
        Compute  = 1u << 2,
    }

    public enum LoadOp { Load = 0, Clear = 1, Discard = 2 }
    public enum StoreOp { Store = 0, Discard = 1 }

    public enum ResourceState
    {
        Undefined        = 0,
        RenderTarget     = 1,
        DepthStencil     = 2,
        ShaderRead       = 3,
        UnorderedAccess  = 4,
        CopySrc          = 5,
        CopyDst          = 6,
        Present          = 7,
    }

    public const uint TextureRenderTarget = 1u << 0;
    public const uint TextureShaderRead    = 1u << 1;
    public const uint TextureCopySrc       = 1u << 2;
    public const uint TextureCopyDst       = 1u << 3;

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
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GraphicsPipelineDesc
    {
        public uint Abi;
        public IntPtr VertexShader;     // RhiShader*
        public IntPtr FragmentShader;
        public TextureFormat ColorFormat;
        public int EnableDepth;
        public int SampleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ComputePipelineDesc
    {
        public uint Abi;
        public IntPtr ComputeShader;
    }

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

    // ---- device / swapchain / resources ----

    [LibraryImport(Library)]
    public static partial int RhiInit(out IntPtr outDevice);   // RhiDevice*

    [LibraryImport(Library)]
    public static partial void RhiShutdown(IntPtr device);

    [LibraryImport(Library)]
    public static partial int RhiCreateSwapchain(IntPtr device,
                                                  IntPtr osWindowHandle,
                                                  uint width, uint height,
                                                  out IntPtr outSwapchain);

    [LibraryImport(Library)]
    public static partial void RhiDestroySwapchain(IntPtr sc);

    [LibraryImport(Library)]
    public static partial uint RhiAcquireNextImage(IntPtr sc,
                                                   out IntPtr outImage);

    [LibraryImport(Library)]
    public static partial int RhiPresent(IntPtr sc);

    [LibraryImport(Library)]
    public static partial int RhiCreateBuffer(IntPtr device,
                                               in BufferDesc desc,
                                               out IntPtr outBuf);

    [LibraryImport(Library)]
    public static partial int RhiCreateTexture(IntPtr device,
                                                in TextureDesc desc,
                                                out IntPtr outTex);

    [LibraryImport(Library)]
    public static partial int RhiCreateShader(IntPtr device,
                                               in ShaderDesc desc,
                                               out IntPtr outShader);

    [LibraryImport(Library)]
    public static partial int RhiCreateGraphicsPipeline(IntPtr device,
                                                        in GraphicsPipelineDesc desc,
                                                        out IntPtr outPipe);

    [LibraryImport(Library)]
    public static partial int RhiCreateComputePipeline(IntPtr device,
                                                       in ComputePipelineDesc desc,
                                                       out IntPtr outPipe);

    [LibraryImport(Library)]
    public static partial void RhiDestroyBuffer(IntPtr buf);

    [LibraryImport(Library)]
    public static partial void RhiDestroyTexture(IntPtr tex);

    [LibraryImport(Library)]
    public static partial void RhiDestroyShader(IntPtr sh);

    [LibraryImport(Library)]
    public static partial void RhiDestroyPipeline(IntPtr p);

    [LibraryImport(Library)]
    public static partial int RhiBufferUpload(IntPtr buf,
                                               IntPtr data,
                                               ulong size);

    [LibraryImport(Library)]
    public static partial int RhiTextureReadback(IntPtr tex,
                                                 IntPtr outBytes,
                                                 ulong outSize,
                                                 uint outStride);

    // ---- command-list / encoders ----

    [LibraryImport(Library)]
    public static partial IntPtr RhiBeginCmdlist(IntPtr device);

    [LibraryImport(Library)]
    public static partial int RhiSubmit(IntPtr device, IntPtr cmdlist);

    [LibraryImport(Library)]
    public static partial void RhiCmdPipelineBarrier(IntPtr cmdlist,
                                                      uint count,
                                                      IntPtr barriers);

    [LibraryImport(Library)]
    public static partial IntPtr RhiBeginRenderPass(IntPtr cmdlist,
                                                    in PassDesc desc);

    [LibraryImport(Library)]
    public static partial IntPtr RhiBeginComputePass(IntPtr cmdlist,
                                                     IntPtr debugName);

    [LibraryImport(Library)]
    public static partial void RhiEndPass(IntPtr encoder);

    [LibraryImport(Library)]
    public static partial void RhiCmdBindPipeline(IntPtr encoder, IntPtr pipeline);

    [LibraryImport(Library)]
    public static partial void RhiCmdBindVertexBuffer(IntPtr encoder,
                                                       uint slot, IntPtr buf,
                                                       ulong offset);

    [LibraryImport(Library)]
    public static partial void RhiCmdBindUniformBuffer(IntPtr encoder,
                                                        uint slot, IntPtr buf);

    [LibraryImport(Library)]
    public static partial void RhiCmdSetViewport(IntPtr encoder,
                                                  float x, float y,
                                                  float w, float h,
                                                  float minDepth, float maxDepth);

    [LibraryImport(Library)]
    public static partial void RhiCmdSetScissor(IntPtr encoder,
                                                 uint x, uint y,
                                                 uint w, uint h);

    [LibraryImport(Library)]
    public static partial void RhiCmdSetClearColor(IntPtr encoder,
                                                    float r, float g,
                                                    float b, float a);

    [LibraryImport(Library)]
    public static partial void RhiCmdDraw(IntPtr encoder, in DrawArgs args);

    [LibraryImport(Library)]
    public static partial void RhiCmdDispatch(IntPtr encoder,
                                               uint gx, uint gy, uint gz);
}
