// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.CBindings;
using ImGuiNET; // Twizzle.ImGui wrapper usually exposes ImGuiNET namespace

namespace Engine.Game;

public sealed class ImGuiRenderer : IDisposable
{
    private readonly RhiDevice _device;
    private RhiShader? _vs;
    private RhiShader? _fs;
    private RhiPipeline? _pipeline;
    private RhiTexture? _fontTexture;
    
    private RhiBuffer? _vertexBuffer;
    private RhiBuffer? _indexBuffer;
    private int _vertexBufferSize;
    private int _indexBufferSize;

    [StructLayout(LayoutKind.Sequential)]
    struct PushConstants
    {
        public Vector2 Scale;
        public Vector2 Translate;
    }

    public ImGuiRenderer(RhiDevice device)
    {
        _device = device;
        IntPtr ctx = ImGui.CreateContext();
        ImGui.SetCurrentContext(ctx);
        var io = ImGui.GetIO();
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        
        CreateFontTexture();
    }
    
    public void LoadShaders(string contentRoot)
    {
        if (_pipeline != null) return;
        
        string shaderPath = Path.Combine(contentRoot, "shaders", "imgui.slang");
        string src = File.ReadAllText(shaderPath);
        _vs = RhiShader.FromSource(_device, src, "vertexMain", RhiNative.ShaderStage.Vertex);
        _fs = RhiShader.FromSource(_device, src, "fragmentMain", RhiNative.ShaderStage.Fragment);
        
        // ImGui uses BGRA or RGBA. Let's assume pipeline is for Bgra8Unorm
        _pipeline = RhiPipeline.CreateGraphics(
            _device, _vs, _fs,
            RhiNative.TextureFormat.Bgra8Unorm,
            enableDepth: false,
            enableBlend: true); // ImGui requires alpha blending
    }
    
    private unsafe void CreateFontTexture()
    {
        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out byte* pixels, out int width, out int height, out int bytesPerPixel);
        
        // Allocate texture
        _fontTexture = RhiTexture.Create2D(_device, (uint)width, (uint)height, RhiNative.TextureFormat.Rgba8Unorm);
        
        // Upload data
        _fontTexture.Upload(new IntPtr(pixels), (uint)(width * height * bytesPerPixel), (uint)(width * bytesPerPixel));
        io.Fonts.SetTexID(new IntPtr(1)); // arbitrary ID
        io.Fonts.ClearTexData();
    }
    
    public void UpdateInput(InputState input, uint width, uint height)
    {
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(input.LogicalWidth > 0 ? input.LogicalWidth : width, input.LogicalHeight > 0 ? input.LogicalHeight : height);
        io.DisplayFramebufferScale = new Vector2(input.RenderScale > 0 ? input.RenderScale : 1.0f);
        io.DeltaTime = input.DeltaTime > 0 ? input.DeltaTime : 1.0f / 60.0f;
        
        io.AddMousePosEvent(input.MouseX, input.MouseY);
        io.AddMouseButtonEvent(0, input.MouseDownLeft);
        io.AddMouseButtonEvent(1, input.MouseDownRight);
        io.AddMouseButtonEvent(2, input.MouseDownMiddle);
        // AddMouseWheelEvent, AddKeyEvent...
    }
    
    public unsafe void Render(ICommandSink sink)
    {
        ImGui.Render();
        var drawData = ImGui.GetDrawData();
        if (drawData.CmdListsCount == 0) return;
        
        // Ensure buffer sizes
        if (_vertexBufferSize < drawData.TotalVtxCount)
        {
            _vertexBuffer?.Dispose();
            _vertexBufferSize = drawData.TotalVtxCount + 5000;
            _vertexBuffer = RhiBuffer.Create(_device, (uint)(_vertexBufferSize * sizeof(ImDrawVert)), RhiNative.BufferUsage.Vertex);
        }
        if (_indexBufferSize < drawData.TotalIdxCount)
        {
            _indexBuffer?.Dispose();
            _indexBufferSize = drawData.TotalIdxCount + 10000;
            _indexBuffer = RhiBuffer.Create(_device, (uint)(_indexBufferSize * sizeof(ushort)), RhiNative.BufferUsage.Index);
        }
        
        // Upload data
        int vtxOffset = 0;
        int idxOffset = 0;
        
        // We'll write to mapped buffers ideally, but for now we'll collect and update
        var vtxData = new ImDrawVert[drawData.TotalVtxCount];
        var idxData = new ushort[drawData.TotalIdxCount];
        
        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            
            var vPtr = (ImDrawVert*)cmdList.VtxBuffer.Data;
            for (int i = 0; i < cmdList.VtxBuffer.Size; i++) vtxData[vtxOffset + i] = vPtr[i];
            
            var iPtr = (ushort*)cmdList.IdxBuffer.Data;
            for (int i = 0; i < cmdList.IdxBuffer.Size; i++) idxData[idxOffset + i] = iPtr[i];
            
            vtxOffset += cmdList.VtxBuffer.Size;
            idxOffset += cmdList.IdxBuffer.Size;
        }
        
        if (_vertexBuffer != null)
        {
            fixed (void* vPtr = vtxData) _vertexBuffer.Upload(new IntPtr(vPtr), (uint)(drawData.TotalVtxCount * sizeof(ImDrawVert)));
        }
        if (_indexBuffer != null)
        {
            fixed (void* iPtr = idxData) _indexBuffer.Upload(new IntPtr(iPtr), (uint)(drawData.TotalIdxCount * sizeof(ushort)));
        }
        
        // Setup push constants
        PushConstants pc;
        pc.Scale = new Vector2(2.0f / drawData.DisplaySize.X, -2.0f / drawData.DisplaySize.Y);
        pc.Translate = new Vector2(-1.0f - drawData.DisplayPos.X * pc.Scale.X, 1.0f - drawData.DisplayPos.Y * pc.Scale.Y);
        
        if (_pipeline != null) sink.BindPipeline(_pipeline);
        if (_vertexBuffer != null) sink.BindVertexBuffer(1, _vertexBuffer, 0);
        if (_indexBuffer != null) sink.BindIndexBuffer(_indexBuffer, false, 0); // false because ImDrawIdx is ushort
        
        sink.PushConstants((uint)sizeof(PushConstants), new IntPtr(&pc));
        
        int globalVtxOffset = 0;
        int globalIdxOffset = 0;
        var clipOff = drawData.DisplayPos;
        var clipScale = drawData.FramebufferScale;
        
        for (int n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            for (int cmd_i = 0; cmd_i < cmdList.CmdBuffer.Size; cmd_i++)
            {
                var pcmd = cmdList.CmdBuffer[cmd_i];
                if (pcmd.UserCallback != IntPtr.Zero)
                {
                    // Use unmanaged function pointer to call Cdecl callback
                    delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> callback = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)pcmd.UserCallback;
                    callback((IntPtr)cmdList.NativePtr, (IntPtr)pcmd.NativePtr);
                    continue;
                }
                
                var clipMin = new Vector2((pcmd.ClipRect.X - clipOff.X) * clipScale.X, (pcmd.ClipRect.Y - clipOff.Y) * clipScale.Y);
                var clipMax = new Vector2((pcmd.ClipRect.Z - clipOff.X) * clipScale.X, (pcmd.ClipRect.W - clipOff.Y) * clipScale.Y);
                if (clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y) continue;
                
                sink.SetScissor((uint)clipMin.X, (uint)clipMin.Y, (uint)(clipMax.X - clipMin.X), (uint)(clipMax.Y - clipMin.Y));
                
                // Texture binding
                if (_fontTexture != null) sink.BindTexture(0, _fontTexture);
                
                // Draw
                sink.DrawIndexed(pcmd.ElemCount, 1, pcmd.IdxOffset + (uint)globalIdxOffset, (int)pcmd.VtxOffset + globalVtxOffset, 0);
            }
            globalIdxOffset += cmdList.IdxBuffer.Size;
            globalVtxOffset += cmdList.VtxBuffer.Size;
        }
    }
    
    public void Dispose()
    {
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _fontTexture?.Dispose();
        _pipeline?.Dispose();
        _vs?.Dispose();
        _fs?.Dispose();
        ImGui.DestroyContext();
    }
}
