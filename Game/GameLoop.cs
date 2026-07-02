// SPDX-License-Identifier: MIT
using System;
using Engine.RHI;
using static Engine.CBindings.Log;
using System.Numerics;
using Engine.Scene.Components;

namespace Engine.Game;

public sealed class GameLoop : IGameLoop
{
    private RhiDevice? _device;
    private RhiSwapchain? _swap;
    private IEntityStore? _world;
    private Renderer? _renderer;
    private ImGuiRenderer? _imguiRenderer;
    private uint _lastWidth = 1280;
    private uint _lastHeight = 720;

    public void Init(IntPtr deviceHandle, IntPtr swapchainHandle, IEntityStore world)
    {
        Info("[GameLoop] Initializing...", "Game");
        _device = new RhiDevice(deviceHandle, ownsHandle: false);
        _swap = new RhiSwapchain(_device, swapchainHandle, ownsHandle: false);
        _world = world;
        _world?.Clear();
        _imguiRenderer = new ImGuiRenderer(_device);
        _renderer = new Renderer(_device, _swap, _world, _imguiRenderer);
        Info("[GameLoop] Initialized successfully", "Game");
    }

    private static void SeedWorld(IEntityStore world)
    {
        // Model loading and entity creation is now handled dynamically.
    }

    private ulong _editorCameraEnt = 0;
    
    private void EnsureCamera()
    {
        if (_world == null) return;
        if (_editorCameraEnt != 0) return;

        _editorCameraEnt = _world.CreateEntity();
        _world.Set(_editorCameraEnt, new Camera 
        { 
            FieldOfView = 60.0f * (MathF.PI / 180.0f),
            NearClip = 0.1f,
            FarClip = 1000.0f
        });
        _world.Set(_editorCameraEnt, Transform.Default with 
        {
            Position = new Vector3(0, 3, -5) // stepped back a bit
        });
    }

    public void Update(InputState input)
    {
        if (_world == null) return;
        EnsureCamera();
        
        if (_imguiRenderer != null)
        {
            _imguiRenderer.UpdateInput(input, _lastWidth, _lastHeight);
            ImGuiNET.ImGui.NewFrame();
            
            // Draw a test window
            ImGuiNET.ImGui.ShowDemoWindow();
        }

        if (_world.TryGet<Transform>(_editorCameraEnt, out var t))
        {
            // Simple euler angles for pitch/yaw
            // We'll store pitch and yaw in some persistent state, or extract from rotation.
            // For a simple implementation, let's keep pitch/yaw locally in GameLoop.
            
            _yaw += input.MouseDeltaX * -0.005f;
            _pitch += input.MouseDeltaY * 0.005f;

            // clamp pitch
            _pitch = Math.Clamp(_pitch, -1.5f, 1.5f);

            var rotation = Quaternion.CreateFromYawPitchRoll(_yaw, _pitch, 0);

            var forward = Vector3.Transform(Vector3.UnitZ, rotation);
            var right = Vector3.Transform(Vector3.UnitX, rotation);
            var move = Vector3.Zero;

            if (input.KeyW) move += forward;
            if (input.KeyS) move -= forward;
            if (input.KeyA) move += right;
            if (input.KeyD) move -= right;

            if (move.LengthSquared() > 0)
                move = Vector3.Normalize(move);

            t.Position += move * 5.0f * input.DeltaTime; // 5 units per second
            t.Rotation = rotation;

            _world.Set(_editorCameraEnt, t);
        }
    }

    private float _pitch;
    private float _yaw;

    public void LoadScene(string contentRoot, string sceneName)
    {
        _imguiRenderer?.LoadShaders(contentRoot);
        _renderer?.LoadScene(contentRoot, sceneName);
        // Re-seed AFTER scene load so game-code edits always override scene defaults.
        // This is what makes hot-reload vertex/color edits take effect:
        // the scene JSON provides fallback geometry, but SeedWorld has final say.
        if (_world is not null)
            SeedWorld(_world);
    }

    public void RenderFrame(RhiTexture backBuffer, uint width, uint height)
    {
        _lastWidth = width;
        _lastHeight = height;
        _renderer?.RenderFrame(backBuffer, width, height);
    }

    public void Dispose()
    {
        _renderer?.Dispose();
        _renderer = null;
        _imguiRenderer?.Dispose();
        _imguiRenderer = null;
        _swap?.Dispose();
        _swap = null;
        _device?.Dispose();
        _device = null;
    }
}
