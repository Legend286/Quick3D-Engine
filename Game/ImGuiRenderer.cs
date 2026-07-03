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
        
        // Key states are now handled via events
    }
    
    public void HandleEvent(NativeInput.EngineInputEvent ev)
    {
        var io = ImGui.GetIO();
        if (ev.Type == 0) // KeyDown
        {
            var igKey = MapKey(ev.Key);
            if (igKey != ImGuiKey.None) io.AddKeyEvent(igKey, true);
        }
        else if (ev.Type == 1) // KeyUp
        {
            var igKey = MapKey(ev.Key);
            if (igKey != ImGuiKey.None) io.AddKeyEvent(igKey, false);
        }
        else if (ev.Type == 2) // MouseMove
        {
            io.AddMousePosEvent(ev.MouseX, ev.MouseY);
        }
        else if (ev.Type == 3) // MouseDown
        {
            io.AddMouseButtonEvent((int)ev.MouseButton, true);
        }
        else if (ev.Type == 4) // MouseUp
        {
            io.AddMouseButtonEvent((int)ev.MouseButton, false);
        }
        else if (ev.Type == 5) // Scroll
        {
            io.AddMouseWheelEvent(ev.ScrollX, ev.ScrollY);
        }
        else if (ev.Type == 6) // Char
        {
            io.AddInputCharacter(ev.CharCode);
        }
    }

    private ImGuiKey MapKey(NativeInput.EngineKey key)
    {
        return key switch
        {
            NativeInput.EngineKey.Space => ImGuiKey.Space,
            NativeInput.EngineKey.Apostrophe => ImGuiKey.Apostrophe,
            NativeInput.EngineKey.Comma => ImGuiKey.Comma,
            NativeInput.EngineKey.Minus => ImGuiKey.Minus,
            NativeInput.EngineKey.Period => ImGuiKey.Period,
            NativeInput.EngineKey.Slash => ImGuiKey.Slash,
            NativeInput.EngineKey.Num0 => ImGuiKey._0,
            NativeInput.EngineKey.Num1 => ImGuiKey._1,
            NativeInput.EngineKey.Num2 => ImGuiKey._2,
            NativeInput.EngineKey.Num3 => ImGuiKey._3,
            NativeInput.EngineKey.Num4 => ImGuiKey._4,
            NativeInput.EngineKey.Num5 => ImGuiKey._5,
            NativeInput.EngineKey.Num6 => ImGuiKey._6,
            NativeInput.EngineKey.Num7 => ImGuiKey._7,
            NativeInput.EngineKey.Num8 => ImGuiKey._8,
            NativeInput.EngineKey.Num9 => ImGuiKey._9,
            NativeInput.EngineKey.Semicolon => ImGuiKey.Semicolon,
            NativeInput.EngineKey.Equal => ImGuiKey.Equal,
            NativeInput.EngineKey.A => ImGuiKey.A,
            NativeInput.EngineKey.B => ImGuiKey.B,
            NativeInput.EngineKey.C => ImGuiKey.C,
            NativeInput.EngineKey.D => ImGuiKey.D,
            NativeInput.EngineKey.E => ImGuiKey.E,
            NativeInput.EngineKey.F => ImGuiKey.F,
            NativeInput.EngineKey.G => ImGuiKey.G,
            NativeInput.EngineKey.H => ImGuiKey.H,
            NativeInput.EngineKey.I => ImGuiKey.I,
            NativeInput.EngineKey.J => ImGuiKey.J,
            NativeInput.EngineKey.K => ImGuiKey.K,
            NativeInput.EngineKey.L => ImGuiKey.L,
            NativeInput.EngineKey.M => ImGuiKey.M,
            NativeInput.EngineKey.N => ImGuiKey.N,
            NativeInput.EngineKey.O => ImGuiKey.O,
            NativeInput.EngineKey.P => ImGuiKey.P,
            NativeInput.EngineKey.Q => ImGuiKey.Q,
            NativeInput.EngineKey.R => ImGuiKey.R,
            NativeInput.EngineKey.S => ImGuiKey.S,
            NativeInput.EngineKey.T => ImGuiKey.T,
            NativeInput.EngineKey.U => ImGuiKey.U,
            NativeInput.EngineKey.V => ImGuiKey.V,
            NativeInput.EngineKey.W => ImGuiKey.W,
            NativeInput.EngineKey.X => ImGuiKey.X,
            NativeInput.EngineKey.Y => ImGuiKey.Y,
            NativeInput.EngineKey.Z => ImGuiKey.Z,
            NativeInput.EngineKey.LeftBracket => ImGuiKey.LeftBracket,
            NativeInput.EngineKey.Backslash => ImGuiKey.Backslash,
            NativeInput.EngineKey.RightBracket => ImGuiKey.RightBracket,
            NativeInput.EngineKey.GraveAccent => ImGuiKey.GraveAccent,
            NativeInput.EngineKey.Escape => ImGuiKey.Escape,
            NativeInput.EngineKey.Enter => ImGuiKey.Enter,
            NativeInput.EngineKey.Tab => ImGuiKey.Tab,
            NativeInput.EngineKey.Backspace => ImGuiKey.Backspace,
            NativeInput.EngineKey.Insert => ImGuiKey.Insert,
            NativeInput.EngineKey.Delete => ImGuiKey.Delete,
            NativeInput.EngineKey.Right => ImGuiKey.RightArrow,
            NativeInput.EngineKey.Left => ImGuiKey.LeftArrow,
            NativeInput.EngineKey.Down => ImGuiKey.DownArrow,
            NativeInput.EngineKey.Up => ImGuiKey.UpArrow,
            NativeInput.EngineKey.PageUp => ImGuiKey.PageUp,
            NativeInput.EngineKey.PageDown => ImGuiKey.PageDown,
            NativeInput.EngineKey.Home => ImGuiKey.Home,
            NativeInput.EngineKey.End => ImGuiKey.End,
            NativeInput.EngineKey.CapsLock => ImGuiKey.CapsLock,
            NativeInput.EngineKey.ScrollLock => ImGuiKey.ScrollLock,
            NativeInput.EngineKey.NumLock => ImGuiKey.NumLock,
            NativeInput.EngineKey.PrintScreen => ImGuiKey.PrintScreen,
            NativeInput.EngineKey.Pause => ImGuiKey.Pause,
            NativeInput.EngineKey.F1 => ImGuiKey.F1,
            NativeInput.EngineKey.F2 => ImGuiKey.F2,
            NativeInput.EngineKey.F3 => ImGuiKey.F3,
            NativeInput.EngineKey.F4 => ImGuiKey.F4,
            NativeInput.EngineKey.F5 => ImGuiKey.F5,
            NativeInput.EngineKey.F6 => ImGuiKey.F6,
            NativeInput.EngineKey.F7 => ImGuiKey.F7,
            NativeInput.EngineKey.F8 => ImGuiKey.F8,
            NativeInput.EngineKey.F9 => ImGuiKey.F9,
            NativeInput.EngineKey.F10 => ImGuiKey.F10,
            NativeInput.EngineKey.F11 => ImGuiKey.F11,
            NativeInput.EngineKey.F12 => ImGuiKey.F12,
            NativeInput.EngineKey.Kp0 => ImGuiKey.Keypad0,
            NativeInput.EngineKey.Kp1 => ImGuiKey.Keypad1,
            NativeInput.EngineKey.Kp2 => ImGuiKey.Keypad2,
            NativeInput.EngineKey.Kp3 => ImGuiKey.Keypad3,
            NativeInput.EngineKey.Kp4 => ImGuiKey.Keypad4,
            NativeInput.EngineKey.Kp5 => ImGuiKey.Keypad5,
            NativeInput.EngineKey.Kp6 => ImGuiKey.Keypad6,
            NativeInput.EngineKey.Kp7 => ImGuiKey.Keypad7,
            NativeInput.EngineKey.Kp8 => ImGuiKey.Keypad8,
            NativeInput.EngineKey.Kp9 => ImGuiKey.Keypad9,
            NativeInput.EngineKey.KpDecimal => ImGuiKey.KeypadDecimal,
            NativeInput.EngineKey.KpDivide => ImGuiKey.KeypadDivide,
            NativeInput.EngineKey.KpMultiply => ImGuiKey.KeypadMultiply,
            NativeInput.EngineKey.KpSubtract => ImGuiKey.KeypadSubtract,
            NativeInput.EngineKey.KpAdd => ImGuiKey.KeypadAdd,
            NativeInput.EngineKey.KpEnter => ImGuiKey.KeypadEnter,
            NativeInput.EngineKey.KpEqual => ImGuiKey.KeypadEqual,
            NativeInput.EngineKey.LeftShift => ImGuiKey.LeftShift,
            NativeInput.EngineKey.LeftCtrl => ImGuiKey.LeftCtrl,
            NativeInput.EngineKey.LeftAlt => ImGuiKey.LeftAlt,
            NativeInput.EngineKey.LeftSuper => ImGuiKey.LeftSuper,
            NativeInput.EngineKey.RightShift => ImGuiKey.RightShift,
            NativeInput.EngineKey.RightCtrl => ImGuiKey.RightCtrl,
            NativeInput.EngineKey.RightAlt => ImGuiKey.RightAlt,
            NativeInput.EngineKey.RightSuper => ImGuiKey.RightSuper,
            NativeInput.EngineKey.Menu => ImGuiKey.Menu,
            _ => ImGuiKey.None,
        };
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
        
        sink.PushConstants(0, (uint)sizeof(PushConstants), new IntPtr(&pc));
        
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
