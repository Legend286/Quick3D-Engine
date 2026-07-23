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
    private bool _enableImGui;
    private bool _imGuiFrameStarted = false;

    public GameLoop() 
    {
        _enableImGui = true;
    }

    public GameLoop(bool enableImGui)
    {
        _enableImGui = enableImGui;
    }

    public void Init(IntPtr deviceHandle, IntPtr swapchainHandle, IEntityStore world, bool enableImGui = true)
    {
        Info("[GameLoop] Initializing...", "Game");
        _device = new RhiDevice(deviceHandle, ownsHandle: false);
        _swap = new RhiSwapchain(_device, swapchainHandle, ownsHandle: false);
        _world = world;
        _enableImGui = enableImGui;
        if (_world != null)
        {
            _world.OnWorldCleared += () => _editorCameraEnt = 0;
            _world.Clear();
        }
        if (_enableImGui)
        {
            _imguiRenderer = new ImGuiRenderer(_device!);
        }
        _renderer = new Renderer(_device!, _swap!, _world!, _imguiRenderer);
        Info("[GameLoop] Initialized successfully", "Game");
    }

    private static void SeedWorld(IEntityStore world)
    {
        // Model loading and entity creation is now handled dynamically.
    }

    private ulong _editorCameraEnt = 0;

    public event Action<ulong>? OnEntityPicked;

    public ulong SelectedEntity
    {
        get => _renderer?.SelectedEntity ?? 0;
        set
        {
            if (_renderer != null) _renderer.SelectedEntity = value;
        }
    }

    public void SetSelectedEntity(ulong entityId)
    {
        SelectedEntity = entityId;
    }

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

        if (_renderer != null)
            _renderer.ActiveCameraEntity = _editorCameraEnt;
    }

    private float _pitch;
    private float _yaw;
    private float _lastMouseX;
    private float _lastMouseY;
    private bool _wasKeyPDown;
    private bool _wasMouseDownLeft;

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

            if (_imGuiFrameStarted)
            {
                ImGuiNET.ImGui.EndFrame();
            }

            ImGuiNET.ImGui.NewFrame();
            _imGuiFrameStarted = true;

            // Draw a test window
            ImGuiNET.ImGui.ShowDemoWindow();
        }

        if (input.MouseDownLeft && !_wasMouseDownLeft && _renderer != null && _lastWidth > 0 && _lastHeight > 0)
        {
            uint px = (uint)Math.Clamp(input.MouseX * input.RenderScale, 0, _lastWidth - 1);
            uint py = (uint)Math.Clamp(input.MouseY * input.RenderScale, 0, _lastHeight - 1);
            
            ulong pickedId = _renderer.Pick(px, py, _lastWidth, _lastHeight);
            SelectedEntity = pickedId; // Update selection
            OnEntityPicked?.Invoke(pickedId);
            if (pickedId != 0)
            {
                Info($"[GameLoop] Picked Entity ID: {pickedId}", "Game");
            }
            else
            {
                Info($"[GameLoop] Picked sky (0)", "Game");
            }
        }
        _wasMouseDownLeft = input.MouseDownLeft;

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
            if (_imGuiFrameStarted)
            {
                ImGuiNET.ImGui.EndFrame();
            }
            throw;
        }
        finally
        {
            _imGuiFrameStarted = false;
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
        // The camera position is overridden below for models and materials to fit them properly
        tempWorld.Set(camEnt, new Transform { Position = new Vector3(0, 0, 2.0f), Rotation = Quaternion.Identity });

        // Note: Lights are populated directly into the dummy SceneGraph in Renderer.BuildThumbnailPlan

        if (assetType == "Model")
        {
            var model = Engine.Assets.ModelLoader.LoadMdl(_device, assetPath);
            ulong modelId = Engine.Assets.AssetRegistry.RegisterModel(model);

            Vector3 min = new Vector3(float.MaxValue);
            Vector3 max = new Vector3(float.MinValue);
            bool hasBounds = false;
            foreach (var part in model.Parts)
            {
                if (part.BoundsMin != Vector3.Zero || part.BoundsMax != Vector3.Zero)
                {
                    min = Vector3.Min(min, part.BoundsMin);
                    max = Vector3.Max(max, part.BoundsMax);
                    hasBounds = true;
                }
            }

            Vector3 offset = Vector3.Zero;
            float scale = 1.0f;
            if (hasBounds)
            {
                Vector3 center = (min + max) * 0.5f;
                Vector3 size = max - min;
                float maxDim = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
                if (maxDim > 0.001f)
                {
                    // Scale so the max dimension fills 100% of FOV
                    scale = 2.2f / maxDim;
                }
                offset = -center * scale;
            }

            ulong ent = tempWorld.CreateEntity();
            tempWorld.Set(ent, Engine.RHI.ModelComponent.Create(modelId));
            tempWorld.Set(ent, new Transform { Position = offset, Scale = new Vector3(scale), Rotation = Quaternion.Identity });
            tempWorld.Set(camEnt, new Transform { Position = new Vector3(0, 0, 2.2f), Rotation = Quaternion.Identity });
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
            
            // Camera position so sphere fills icon completely
            tempWorld.Set(camEnt, new Transform { Position = new Vector3(0, 0, 1.8f), Rotation = Quaternion.Identity });
        }

        else if (assetType == "Texture")
        {
            string planePath = System.IO.Path.Combine(contentRoot, ".cache", "thumbnails", "plane.msh");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(planePath)!);
            if (!System.IO.File.Exists(planePath))
            {
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

            var mat = new Engine.Assets.Material
            {
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

            // Rotate plane to face camera (-Z view)
            tempWorld.Set(ent, new Transform { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2.0f) });
            tempWorld.Set(camEnt, new Transform { Position = new Vector3(0, 0, 1.8f), Rotation = Quaternion.Identity });
        }

        // 4. Render to target using a temporary Renderer instance
        using var tempRenderer = new Renderer(_device, _swap!, tempWorld, null);
        tempRenderer.ActiveCameraEntity = camEnt;
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

    private ulong _previewMatId;

    public void LoadMaterialPreview(string contentRoot, string materialPath)
    {
        if (_device == null || _world == null || _renderer == null) return;
        _world.Clear();

        // 1. Camera
        ulong camEnt = _world.CreateEntity();
        _world.Set(camEnt, new Engine.Scene.Components.Camera { FieldOfView = 60.0f * (MathF.PI / 180.0f), NearClip = 0.1f, FarClip = 100.0f });
        _world.Set(camEnt, new Transform { Position = new Vector3(0, 0, 3.0f), Rotation = Quaternion.Identity });
        _renderer.ActiveCameraEntity = camEnt;

        // 2. Sphere Model
        string spherePath = System.IO.Path.Combine(contentRoot, ".cache", "thumbnails", "sphere.msh");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(spherePath)!);
        if (!System.IO.File.Exists(spherePath))
        {
            Engine.Game.PrimitiveMeshFactory.GenerateUVSphere(spherePath);
        }
        var mesh = Engine.Assets.MeshLoader.LoadMsh(_device, spherePath);
        ulong meshId = Engine.Assets.AssetRegistry.RegisterMesh(mesh);

        var mat = Engine.Assets.MaterialLoader.LoadMat(_device, materialPath);
        ulong matId = Engine.Assets.AssetRegistry.RegisterMaterial(mat);
        _previewMatId = matId;

        var model = new Engine.Assets.Model();
        model.Parts = new[] { new Engine.Assets.ModelPart { Mesh = mesh, MeshId = meshId, MaterialId = matId } };
        ulong modelId = Engine.Assets.AssetRegistry.RegisterModel(model);

        ulong ent = _world.CreateEntity();
        _world.Set(ent, Engine.RHI.ModelComponent.Create(modelId));
        _world.Set(ent, Transform.Default);

        _renderer.UsePathTracer = true; // Preview using path tracing
    }

    public void UpdateMaterialPreview(float[] albedo, float metallic, float roughness, float subsurface, float[] subsurfaceColor, float[] subsurfaceRadius, float clearcoat, float clearcoatRoughness, float[] topColor, float topMetallic, float topRoughness, uint topMaskType, float noiseScale = 10.0f, float noiseThresholdMin = 0.3f, float noiseThresholdMax = 0.7f, float[]? layer2Color = null, float layer2Metallic = 0.0f, float layer2Roughness = 1.0f, uint layer2MaskType = 0, float layer2NoiseScale = 10.0f, float layer2NoiseMin = 0.3f, float layer2NoiseMax = 0.7f)
    {
        if (_previewMatId == 0) return;
        var mat = Engine.Assets.AssetRegistry.GetMaterial(_previewMatId);
        if (mat != null)
        {
            mat.AlbedoColor = albedo;
            mat.Metallic = metallic;
            mat.Roughness = roughness;
            mat.Subsurface = subsurface;
            mat.SubsurfaceColor = subsurfaceColor;
            mat.SubsurfaceRadius = subsurfaceRadius;
            mat.Clearcoat = clearcoat;
            mat.ClearcoatRoughness = clearcoatRoughness;
            mat.TopColor = topColor;
            mat.TopMetallic = topMetallic;
            mat.TopRoughness = topRoughness;
            mat.TopMaskType = topMaskType;
            mat.NoiseScale = noiseScale;
            mat.NoiseThresholdMin = noiseThresholdMin;
            mat.NoiseThresholdMax = noiseThresholdMax;
            mat.Layer2Color = layer2Color ?? new float[] { 1, 1, 1, 1 };
            mat.Layer2Metallic = layer2Metallic;
            mat.Layer2Roughness = layer2Roughness;
            mat.Layer2MaskType = layer2MaskType;
            mat.Layer2NoiseScale = layer2NoiseScale;
            mat.Layer2NoiseThresholdMin = layer2NoiseMin;
            mat.Layer2NoiseThresholdMax = layer2NoiseMax;
        }
    }

    public void ApplyMaterialToSubmesh(uint x, uint y, uint w, uint h, string materialPath)
    {
        if (_renderer == null || _device == null) return;
        (ulong entId, uint partIdx) = _renderer.PickSubmesh(x, y, w, h);
        if (entId != 0 && _world.TryGet<Engine.RHI.ModelComponent>(entId, out var modelComp))
        {
            var model = Engine.Assets.AssetRegistry.GetModel(modelComp.ModelId);
            if (model != null && model.Parts != null && partIdx < model.Parts.Length)
            {
                var mat = Engine.Assets.MaterialLoader.LoadMat(_device, materialPath);
                ulong matId = Engine.Assets.AssetRegistry.RegisterMaterial(mat);
                model.Parts[partIdx].MaterialId = matId;
                model.Parts[partIdx].Material = mat;
            }
        }
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
