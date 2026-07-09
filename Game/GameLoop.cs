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
        if (_world != null)
        {
            _world.OnWorldCleared += () => _editorCameraEnt = 0;
            _world.Clear();
        }
        _imguiRenderer = new ImGuiRenderer(_device!);
        _renderer = new Renderer(_device!, _swap!, _world!, _imguiRenderer);
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
            Position = new Vector3(0, 5, -15) // stepped back a bit
        });
    }

    public void Update(InputState input)
    {
        if (_world == null) return;
        EnsureCamera();
        
        // Toggle between path tracer and rasterizer with P key
        if (input.KeyP && !_wasKeyPDown)
        {
            _renderer!.UsePathTracer = !_renderer.UsePathTracer;
            var mode = _renderer.UsePathTracer ? "Path Tracer" : "Rasterizer (PBR)";
            Info($"[GameLoop] Switched to {mode}", "Game");
        }
        _wasKeyPDown = input.KeyP;
        
        if (_imguiRenderer != null)
        {
            _imguiRenderer.UpdateInput(input, _lastWidth, _lastHeight);
            
            if (input.Events != null)
            {
                foreach (var ev in input.Events)
                {
                    _imguiRenderer.HandleEvent(ev);
                }
            }
            
            ImGuiNET.ImGui.NewFrame();
            
            // Draw a test window
            ImGuiNET.ImGui.ShowDemoWindow();
        }

        if (_world.TryGet<Transform>(_editorCameraEnt, out var t))
        {
            float mx = input.MouseX;
            float my = input.MouseY;
            if (input.MouseDownRight)
            {
                var dx = mx - _lastMouseX;
                var dy = my - _lastMouseY;
                _yaw += dx * -0.005f;
                _pitch += dy * 0.005f;
                _pitch = Math.Clamp(_pitch, -1.5f, 1.5f);
            }
            _lastMouseX = mx;
            _lastMouseY = my;

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
    private float _lastMouseX;
    private float _lastMouseY;
    private bool _wasKeyPDown;

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
        try
        {
            _renderer?.RenderFrame(backBuffer, width, height);
        }
        catch
        {
            ImGuiNET.ImGui.EndFrame();
            throw;
        }
    }

    public void RenderThumbnail(string contentRoot, string assetPath, string assetType, RhiTexture target)
    {
        if (_device == null) return;
        
        // 1. Create a temporary world and renderer for the thumbnail pass
        var tempWorld = new EcsWorld();
        
        // 2. Setup the camera and 3-point lighting
        ulong camEnt = tempWorld.CreateEntity();
        tempWorld.Set(camEnt, new Engine.Scene.Components.Camera { FieldOfView = 60.0f * (MathF.PI / 180.0f), NearClip = 0.1f, FarClip = 100.0f });
        tempWorld.Set(camEnt, new Transform { Position = new Vector3(0, 0, 3.0f), Rotation = Quaternion.Identity });

        // Note: Lights are populated directly into the dummy SceneGraph in Renderer.BuildThumbnailPlan

        // 3. Spawn the requested asset
        if (assetType == "Model")
        {
            var model = Engine.Assets.ModelLoader.LoadMdl(_device, assetPath);
            ulong modelId = Engine.Assets.AssetRegistry.RegisterModel(model);
            ulong ent = tempWorld.CreateEntity();
            tempWorld.Set(ent, Engine.RHI.ModelComponent.Create(modelId));
            tempWorld.Set(ent, Transform.Default);
        }
        else if (assetType == "Material")
        {
            // 1. Generate sphere mesh file
            string spherePath = System.IO.Path.Combine(contentRoot, ".cache", "thumbnails", "sphere.msh");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(spherePath)!);
            if (!System.IO.File.Exists(spherePath))
            {
                Engine.Game.PrimitiveMeshFactory.GenerateUVSphere(spherePath);
            }
            // 2. Load mesh and create a dynamic model
            var mesh = Engine.Assets.MeshLoader.LoadMsh(_device, spherePath);
            ulong meshId = Engine.Assets.AssetRegistry.RegisterMesh(mesh);
            
            // 3. Load material
            var mat = Engine.Assets.MaterialLoader.LoadMat(_device, assetPath);
            ulong matId = Engine.Assets.AssetRegistry.RegisterMaterial(mat);

            var model = new Engine.Assets.Model();
            model.Parts = new[] { new Engine.Assets.ModelPart { Mesh = mesh, MeshId = meshId, MaterialId = matId } };
            ulong modelId = Engine.Assets.AssetRegistry.RegisterModel(model);

            ulong ent = tempWorld.CreateEntity();
            tempWorld.Set(ent, Engine.RHI.ModelComponent.Create(modelId));
            tempWorld.Set(ent, Transform.Default);
        }

        else if (assetType == "Texture")
        {
            string planePath = System.IO.Path.Combine(contentRoot, ".cache", "thumbnails", "plane.msh");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(planePath)!);
            if (!System.IO.File.Exists(planePath))
            {
                // Note: using GeneratePlane so we get a flat surface facing up (Y=0).
                Engine.Game.PrimitiveMeshFactory.GeneratePlane(planePath, 2.0f, 2.0f);
            }
            var mesh = Engine.Assets.MeshLoader.LoadMsh(_device, planePath);
            ulong meshId = Engine.Assets.AssetRegistry.RegisterMesh(mesh);

            // Load texture
            Engine.RHI.RhiTexture? t = null;
            if (System.IO.File.Exists(assetPath))
            {
                t = Engine.Assets.TextureLoader.LoadTexture(_device, assetPath);
            }

            // Create an unlit material
            var mat = new Engine.Assets.Material { 
                AlbedoColor = new float[] { 1, 1, 1, 1 }, 
                Metallic = 0.0f, 
                Roughness = 1.0f, 
                AlbedoTexture = t 
            };
            ulong matId = Engine.Assets.AssetRegistry.RegisterMaterial(mat);

            var model = new Engine.Assets.Model();
            model.Parts = new[] { new Engine.Assets.ModelPart { Mesh = mesh, MeshId = meshId, MaterialId = matId } };
            ulong modelId = Engine.Assets.AssetRegistry.RegisterModel(model);

            ulong ent = tempWorld.CreateEntity();
            tempWorld.Set(ent, Engine.RHI.ModelComponent.Create(modelId));
            
            // Rotate the plane to face the camera (camera is at Z=3 looking at Z=0). Plane normal is +Y.
            // So we rotate around X axis by 90 degrees.
            tempWorld.Set(ent, new Transform { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2.0f) });
        }

        // 4. Render to target using a temporary Renderer instance
        // Swapchain can be null/fake since we are overriding backbuffer
        using var tempRenderer = new Renderer(_device, _swap!, tempWorld, null);
        tempRenderer.BuildThumbnailPlan(contentRoot);
        try
        {
            tempRenderer.RenderFrame(target, 256, 256);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameLoop] RenderThumbnail failed: {ex.Message}");
        }
        tempWorld.Dispose();
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
