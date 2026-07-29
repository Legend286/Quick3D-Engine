// SPDX-License-Identifier: MIT
using System;
using Engine.CBindings;
using Engine.RHI;
using static Engine.CBindings.Log;
using System.Numerics;
using Engine.Scene.Components;

namespace Engine.Game;

public sealed class GameLoop : IGameLoop
{
    private const float ModelPreviewYaw = -0.55f;
    private const float ModelPreviewPitch = -0.26f;
    private const float MaterialPreviewYaw = -0.35f;
    private const float MaterialPreviewPitch = -0.18f;

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
            _imguiRenderer.BeginFrame(input, _lastWidth, _lastHeight, input.Events, ref _imGuiFrameStarted);
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

    public ulong AddPointLight(Vector3 position, Vector3 color, float intensity, float range, float sourceRadius, bool castShadows = true)
    {
        if (_renderer == null) return 0;
        return _renderer.AddPointLight(position, color, intensity, range, sourceRadius, castShadows);
    }

    public ulong AddSpotLight(Vector3 position, Vector3 direction, Vector3 color, float intensity, float range, float innerCone, float outerCone, float sourceRadius, bool castShadows = true)
    {
        if (_renderer == null) return 0;
        return _renderer.AddSpotLight(position, direction, color, intensity, range, innerCone, outerCone, sourceRadius, castShadows);
    }

    public void ReplaceSwapchain(RhiSwapchain swapchain)
    {
        var oldSwap = _swap;
        _swap = swapchain;
        oldSwap?.Dispose();
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
            _imguiRenderer?.CancelFrame(ref _imGuiFrameStarted);
            throw;
        }
        finally
        {
            _imGuiFrameStarted = false;
        }
    }

    public void RenderThumbnail(string contentRoot, string assetPath, string assetType, RhiTexture target, uint width = 256, uint height = 256, float orbitRadians = 0.0f, RhiFence? syncFence = null, ulong waitValue = 0, ulong signalValue = 0)
    {
        if (_device == null) return;

        var tempWorld = new EcsWorld();
        ulong camEnt = tempWorld.CreateEntity();
        const float thumbnailFovY = 40.0f * (MathF.PI / 180.0f);
        float thumbnailAspect = height > 0 ? width / (float)height : 1.0f;
        tempWorld.Set(camEnt, new Engine.Scene.Components.Camera { FieldOfView = thumbnailFovY, NearClip = 0.1f, FarClip = 100.0f });
        tempWorld.Set(camEnt, new Transform { Position = new Vector3(0, 0, -3.0f), Rotation = Quaternion.Identity });

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

            Vector3 center = Vector3.Zero;
            Vector3 size = Vector3.One;
            if (hasBounds)
            {
                center = (min + max) * 0.5f;
                size = Vector3.Max(max - min, new Vector3(0.001f));
            }

            float radius = size.Length() * 0.5f;
            float halfFovY = thumbnailFovY * 0.5f;
            float halfFovX = MathF.Atan(MathF.Tan(halfFovY) * thumbnailAspect);
            float distance = radius / MathF.Min(MathF.Tan(halfFovY), MathF.Tan(halfFovX));
            distance += radius * 0.35f;

            Quaternion previewRotation = Quaternion.CreateFromYawPitchRoll(ModelPreviewYaw + orbitRadians, ModelPreviewPitch, 0f);
            Vector3 previewPosition = -Vector3.Transform(center, previewRotation);

            ulong ent = tempWorld.CreateEntity();
            tempWorld.Set(ent, Engine.RHI.ModelComponent.Create(modelId));
            tempWorld.Set(ent, new Transform { Position = previewPosition, Scale = Vector3.One, Rotation = previewRotation });
            tempWorld.Set(camEnt, new Transform { Position = new Vector3(0, 0, -MathF.Max(distance, 1.5f)), Rotation = Quaternion.Identity });
        }
        else if (assetType == "Material")
        {
            string spherePath = System.IO.Path.Combine(contentRoot, ".cache", "thumbnails", "sphere.msh");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(spherePath)!);
            if (!System.IO.File.Exists(spherePath))
            {
                Engine.Game.PrimitiveMeshFactory.GenerateUVSphere(spherePath);
            }
            var mesh = Engine.Assets.MeshLoader.LoadMsh(_device, spherePath);
            ulong meshId = Engine.Assets.AssetRegistry.RegisterMesh(mesh);

            var mat = Engine.Assets.MaterialLoader.LoadMat(_device, assetPath);
            ulong matId = Engine.Assets.AssetRegistry.RegisterMaterial(mat);

            var model = new Engine.Assets.Model();
            model.Parts = new[] { new Engine.Assets.ModelPart { Mesh = mesh, MeshId = meshId, MaterialId = matId } };
            ulong modelId = Engine.Assets.AssetRegistry.RegisterModel(model);

            ulong ent = tempWorld.CreateEntity();
            tempWorld.Set(ent, Engine.RHI.ModelComponent.Create(modelId));
            tempWorld.Set(ent, new Transform
            {
                Position = Vector3.Zero,
                Rotation = Quaternion.CreateFromYawPitchRoll(MaterialPreviewYaw + orbitRadians, MaterialPreviewPitch, 0f),
                Scale = Vector3.One
            });
            tempWorld.Set(camEnt, new Transform { Position = new Vector3(0, 0, -3.6f), Rotation = Quaternion.Identity });
        }
        else if (assetType == "Texture")
        {
            using var textureRenderer = new Renderer(_device, _swap!, tempWorld, null);
            var sourceTexture = Engine.Assets.TextureLoader.LoadTexture(_device, assetPath);
            if (sourceTexture == null)
                return;

            textureRenderer.BuildTextureThumbnailPlan(contentRoot, sourceTexture);
            textureRenderer.RenderFrame(target, width, height, syncFence, waitValue, signalValue);
            tempWorld.Dispose();
            return;
        }

        using var tempRenderer = new Renderer(_device, _swap!, tempWorld, null);
        tempRenderer.ActiveCameraEntity = camEnt;
        tempRenderer.BuildThumbnailPlan(contentRoot);

        try
        {
            tempRenderer.RenderFrame(target, width, height, syncFence, waitValue, signalValue);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameLoop] RenderThumbnail failed: {ex.Message}");
        }
        tempWorld.Dispose();
    }

    private ulong _previewMatId;
    private ulong _previewCameraEntity;
    private ulong _previewEntity;
    private Vector3 _previewCenter;
    private float _previewDistance = 3.0f;
    private string? _previewAssetType;

    public void LoadModelPreview(string contentRoot, string modelPath)
    {
        if (_device == null || _world == null || _renderer == null) return;
        _world.Clear();
        _previewAssetType = "Model";
        _previewMatId = 0;
        _previewEntity = 0;
        _previewCenter = Vector3.Zero;
        _previewDistance = 3.0f;

        ulong camEnt = _world.CreateEntity();
        const float previewFovY = 40.0f * (MathF.PI / 180.0f);
        _world.Set(camEnt, new Engine.Scene.Components.Camera { FieldOfView = previewFovY, NearClip = 0.1f, FarClip = 100.0f });
        _world.Set(camEnt, new Transform { Position = new Vector3(0, 0, -4.0f), Rotation = Quaternion.Identity });
        _renderer.ActiveCameraEntity = camEnt;
        _previewCameraEntity = camEnt;

        var model = Engine.Assets.ModelLoader.LoadMdl(_device, modelPath);
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

        Vector3 center = Vector3.Zero;
        Vector3 size = Vector3.One;
        if (hasBounds)
        {
            center = (min + max) * 0.5f;
            size = Vector3.Max(max - min, new Vector3(0.001f));
        }
        _previewCenter = center;

        float radius = size.Length() * 0.5f;
        float halfFovY = previewFovY * 0.5f;
        float halfFovX = MathF.Atan(MathF.Tan(halfFovY));
        float distance = radius / MathF.Min(MathF.Tan(halfFovY), MathF.Tan(halfFovX));
        distance += radius * 0.4f;
        _previewDistance = MathF.Max(distance, 2.0f);

        Quaternion previewRotation = Quaternion.CreateFromYawPitchRoll(ModelPreviewYaw, ModelPreviewPitch, 0f);
        Vector3 previewPosition = -Vector3.Transform(center, previewRotation);

        ulong ent = _world.CreateEntity();
        _previewEntity = ent;
        _world.Set(ent, Engine.RHI.ModelComponent.Create(modelId));
        _world.Set(ent, new Transform
        {
            Position = previewPosition,
            Rotation = previewRotation,
            Scale = Vector3.One
        });

        _world.Set(camEnt, new Transform { Position = new Vector3(0, 0, -_previewDistance), Rotation = Quaternion.Identity });
        _renderer.UsePathTracer = false;
        _renderer.BuildThumbnailPlan(contentRoot);
    }

    public void LoadMaterialPreview(string contentRoot, string materialPath, bool usePathTracer = true)
    {
        if (_device == null || _world == null || _renderer == null) return;
        _world.Clear();
        _previewAssetType = "Material";
        _previewEntity = 0;
        _previewCenter = Vector3.Zero;
        _previewDistance = 3.25f;

        ulong camEnt = _world.CreateEntity();
        _world.Set(camEnt, new Engine.Scene.Components.Camera { FieldOfView = 60.0f * (MathF.PI / 180.0f), NearClip = 0.1f, FarClip = 100.0f });
        _world.Set(camEnt, new Transform { Position = new Vector3(0, 0, -3.0f), Rotation = Quaternion.Identity });
        _renderer.ActiveCameraEntity = camEnt;
        _previewCameraEntity = camEnt;

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
        _previewEntity = ent;
        _world.Set(ent, Engine.RHI.ModelComponent.Create(modelId));
        _world.Set(ent, new Transform
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.CreateFromYawPitchRoll(MaterialPreviewYaw, MaterialPreviewPitch, 0f),
            Scale = Vector3.One
        });

        _renderer.UsePathTracer = usePathTracer;
        _renderer.BuildThumbnailPlan(contentRoot);
    }

    public void RenderLoadedPreview(RhiTexture target, uint width = 256, uint height = 256, float orbitRadians = 0.0f, RhiFence? syncFence = null, ulong waitValue = 0, ulong signalValue = 0)
    {
        if (_world == null || _renderer == null || _previewCameraEntity == 0 || _previewEntity == 0)
            return;

        if (_world.TryGet<Transform>(_previewEntity, out var previewTransform))
        {
            Quaternion rotation = _previewAssetType == "Material"
                ? Quaternion.CreateFromYawPitchRoll(MaterialPreviewYaw + orbitRadians, MaterialPreviewPitch, 0f)
                : Quaternion.CreateFromYawPitchRoll(ModelPreviewYaw + orbitRadians, ModelPreviewPitch, 0f);

            previewTransform.Rotation = rotation;
            previewTransform.Position = _previewAssetType == "Model"
                ? -Vector3.Transform(_previewCenter, rotation)
                : Vector3.Zero;
            _world.Set(_previewEntity, previewTransform);
        }

        if (_world.TryGet<Transform>(_previewCameraEntity, out var cameraTransform))
        {
            cameraTransform.Position = new Vector3(0, 0, -_previewDistance);
            cameraTransform.Rotation = Quaternion.Identity;
            _world.Set(_previewCameraEntity, cameraTransform);
        }

        _renderer.ActiveCameraEntity = _previewCameraEntity;
        _renderer.RenderFrame(target, width, height, syncFence, waitValue, signalValue);
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
