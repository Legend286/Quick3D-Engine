using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.RenderGraph.Shaders;

namespace Engine.Renderer.VirtualShadowMaps;

[StructLayout(LayoutKind.Sequential)]
public struct VsmRequestConstants
{
    public Matrix4x4 LightViewProj;
    public uint VirtualPagesX;
    public uint VirtualPagesY;
    public uint ScreenDimensions; // Packs width and height into 2x16 bit if needed, or two uints. Let's use two uints.
    public uint ScreenHeight;
}

[StructLayout(LayoutKind.Sequential)]
public struct VsmAllocateConstants
{
    public uint TotalVirtualPages;
}

public sealed class VsmSystem : IDisposable
{
    private const uint VirtualTextureSize = 8192; // Massive 8Kx8K virtual texture
    private const uint VirtualPageDim = 128; // Assuming 128x128 pixels for a 32-bit depth tile (64KB)
    private const uint VirtualPagesX = VirtualTextureSize / VirtualPageDim;
    private const uint VirtualPagesY = VirtualTextureSize / VirtualPageDim;
    private const uint TotalVirtualPages = VirtualPagesX * VirtualPagesY;

    private readonly RhiDevice _device;
    private readonly RhiBindlessHeap _bindlessHeap;
    
    public VsmAllocator Allocator { get; }
    public RhiTexture VirtualShadowTexture { get; }
    public uint VirtualShadowTextureSlot { get; }

    public RhiBuffer PageRequestsBuffer { get; }
    public RhiBuffer AllocateQueueBuffer { get; }

    private readonly RhiShader _requestShader;
    private readonly RhiPipeline _requestPipeline;
    private readonly RhiShader _allocateShader;
    private readonly RhiPipeline _allocatePipeline;

    private bool _isDisposed;

    public VsmSystem(
        RhiDevice device, 
        string contentRoot, 
        RhiBindlessHeap bindlessHeap,
        ShaderCompileCache? compileCache = null)
    {
        _device = device;
        _bindlessHeap = bindlessHeap;

        Allocator = new VsmAllocator(device, 2048); // 128MB physical

        var texDesc = new RhiNative.TextureDesc
        {
            Abi = 1,
            Width = VirtualTextureSize,
            Height = VirtualTextureSize,
            MipLevels = 1,
            Format = RhiNative.TextureFormat.Depth32Float,
            UsageFlags = RhiNative.TextureRenderTarget | 
                         RhiNative.TextureShaderRead | 
                         RhiNative.TextureSparse
        };

        int rc = RhiNative.RhiCreateTexture(device.Handle, in texDesc, out IntPtr texHandle);
        if (rc != 0) throw new InvalidOperationException($"Failed to create VSM sparse texture. rc={rc}");
        
        VirtualShadowTexture = (RhiTexture)Activator.CreateInstance(
            typeof(RhiTexture), 
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null, new object[] { texHandle, true }, null)!;
        VirtualShadowTextureSlot = _bindlessHeap.Register(VirtualShadowTexture);

        PageRequestsBuffer = RhiBuffer.Create(device, TotalVirtualPages * 4, RhiNative.BufferUsage.Storage);
        PageRequestsBuffer.SetDebugName("VSM Page Requests", "Buffer");

        AllocateQueueBuffer = RhiBuffer.Create(device, (TotalVirtualPages + 1) * 4, RhiNative.BufferUsage.Storage);
        AllocateQueueBuffer.SetDebugName("VSM Allocate Queue", "Buffer");

        var includes = new[] { Path.Combine(contentRoot, "shaders") };

        string reqSource = LoadShaderSource(contentRoot, "vsm_request.slang", includes);
        _requestShader = Compile(device, reqSource, "main", RhiNative.ShaderStage.Compute, null, includes, compileCache);
        _requestPipeline = RhiPipeline.CreateCompute(device, _requestShader);
        
        string allocSource = LoadShaderSource(contentRoot, "vsm_allocate.slang", includes);
        _allocateShader = Compile(device, allocSource, "main", RhiNative.ShaderStage.Compute, null, includes, compileCache);
        _allocatePipeline = RhiPipeline.CreateCompute(device, _allocateShader);
    }

    public void RecordRequestPass(RenderGraphBuilder graph)
    {
        // Setup transient writes to PageRequestsBuffer
    }

    public void RecordAllocatePass(RenderGraphBuilder graph)
    {
        // Setup transient writes to AllocateQueueBuffer
    }

    public void ExecuteRequestPass(ICommandSink sink, RenderGraphContext context, RhiTexture depthTexture, Matrix4x4 lightMatrix)
    {
        sink.BeginComputePass("VSM Page Request");
        sink.BindPipeline(_requestPipeline);

        // Bind resources
        sink.BindTexture(0, depthTexture);
        sink.UseBuffer(PageRequestsBuffer, 1);
        
        VsmRequestConstants constants = new VsmRequestConstants
        {
            LightViewProj = lightMatrix,
            VirtualPagesX = VirtualPagesX,
            VirtualPagesY = VirtualPagesY,
            ScreenDimensions = context.Width,
            ScreenHeight = context.Height
        };

        unsafe
        {
            sink.PushConstants(2, (uint)sizeof(VsmRequestConstants), (IntPtr)(&constants));
        }

        sink.Dispatch((context.Width + 7) / 8, (context.Height + 7) / 8, 1);
        sink.EndComputePass();
    }

    public void ExecuteAllocatePass(ICommandSink sink)
    {
        sink.BeginComputePass("VSM Allocate");
        sink.BindPipeline(_allocatePipeline);

        sink.UseBuffer(PageRequestsBuffer, 1);
        sink.UseBuffer(AllocateQueueBuffer, 1);
        
        VsmAllocateConstants constants = new VsmAllocateConstants { TotalVirtualPages = TotalVirtualPages };
        
        unsafe
        {
            sink.PushConstants(2, (uint)sizeof(VsmAllocateConstants), (IntPtr)(&constants));
        }

        sink.Dispatch((TotalVirtualPages + 63) / 64, 1, 1);
        sink.EndComputePass();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _requestPipeline.Dispose();
        _allocatePipeline.Dispose();
        
        PageRequestsBuffer.Dispose();
        AllocateQueueBuffer.Dispose();
        
        if (VirtualShadowTextureSlot != 0)
            _bindlessHeap.Release(VirtualShadowTextureSlot);
            
        VirtualShadowTexture.Dispose();
        Allocator.Dispose();
    }

    private static string LoadShaderSource(string contentRoot, string fileName, IReadOnlyList<string> includeDirs)
    {
        string projectPath = Path.Combine(contentRoot, "shaders", fileName);
        if (File.Exists(projectPath)) return File.ReadAllText(projectPath);
        foreach (string includeDir in includeDirs)
        {
            string includePath = Path.Combine(includeDir, fileName);
            if (File.Exists(includePath)) return File.ReadAllText(includePath);
        }
        throw new FileNotFoundException($"Shader '{fileName}' was not found.", projectPath);
    }

    private static RhiShader Compile(
        RhiDevice device, string source, string entryPoint, RhiNative.ShaderStage stage,
        IReadOnlyList<string>? cliArgs, IReadOnlyList<string>? includeDirs, ShaderCompileCache? compileCache)
    {
        if (compileCache == null)
            return RhiShader.FromSource(device, source, entryPoint, stage, includeDirs, cliArgs);
        
        return (RhiShader)compileCache.GetOrCompileHash(
            source, entryPoint, stage, includeDirs, cliArgs,
            () => RhiShader.FromSource(device, source, entryPoint, stage, includeDirs, cliArgs));
    }
}
