// SPDX-License-Identifier: MIT
using System;
using Engine.CBindings;
using Engine.RHI;
using static Engine.CBindings.Log;
using System.Numerics;
using Engine.Scene;
using Engine.Scene.Components;
using Camera = Engine.Scene.Components.Camera;
using ImGuizmoNET;

namespace Engine.Game;

public sealed class GameLoop : IGameLoop
{
    private const float ModelPreviewYaw = -0.55f;
    private const float ModelPreviewPitch = 0.0f;
    private const float MaterialPreviewYaw = -0.35f;
    private const float MaterialPreviewPitch = 0.0f;
    private const float PreviewCameraElevationRadians = 0.12f;
    private const float PreviewFieldOfViewYRadians =
        40.0f * (MathF.PI / 180.0f);
    private const float ModelPreviewViewportFill = 0.78f;
    private const float MaterialPreviewViewportFill = 0.9f;

    private RhiDevice? _device;
    private RhiSwapchain? _swap;
    private IEntityStore? _world;
    private Renderer? _renderer;
    private ImGuiRenderer? _imguiRenderer;
    private RendererPluginRuntime? _pluginRuntime;
    private uint _lastWidth = 1280;
    private uint _lastHeight = 720;
    private bool _enableImGui;
    private float _editorFieldOfViewDegrees = 60.0f;
    private bool _gizmoWasUsing;
    private bool _gizmoConsumesPointer;
    private ulong _gizmoEntity;

    private static Vector3 GetPreviewPivotPosition(
        Vector3 boundsCenter,
        Quaternion rotation)
        => -Vector3.Transform(boundsCenter, rotation);

    private static Transform GetPreviewCameraTransform(float distance)
        => new()
        {
            Position = new Vector3(
                0.0f,
                MathF.Sin(PreviewCameraElevationRadians) * distance,
                -MathF.Cos(PreviewCameraElevationRadians) * distance),
            Rotation = Quaternion.CreateFromAxisAngle(
                Vector3.UnitX,
                PreviewCameraElevationRadians),
            Scale = Vector3.One
        };

    private static float GetSpherePreviewDistance(
        float radius,
        float aspect,
        float viewportFill)
    {
        float halfFovY = PreviewFieldOfViewYRadians * 0.5f;
        float halfFovX = MathF.Atan(MathF.Tan(halfFovY) * aspect);
        float limitingHalfFov = MathF.Min(halfFovY, halfFovX);
        float framedHalfAngle = MathF.Atan(
            MathF.Tan(limitingHalfFov) *
            viewportFill);
        return radius / MathF.Sin(framedHalfAngle);
    }

    private static Camera GetPreviewCamera(
        float radius,
        float distance)
    {
        float nearClip = MathF.Max(
            distance - radius * 1.25f,
            MathF.Max(radius * 0.01f, 0.00001f));
        return new Camera
        {
            FieldOfView = PreviewFieldOfViewYRadians,
            NearClip = nearClip,
            FarClip = distance + radius * 1.25f
        };
    }

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
            _imguiRenderer.DrawViewportOverlay =
                DrawEditorGizmo;
        }
        _pluginRuntime =
            new RendererPluginRuntime();
        IRendererPlanPlugin clusteredPlugin =
            _pluginRuntime.LoadClustered()
            ?? throw new InvalidOperationException(
                "Required clustered renderer plugin could not be loaded.");
        _renderer = new Renderer(
            _device!,
            _swap!,
            _world!,
            _imguiRenderer,
            clusteredPlugin);
        Info("[GameLoop] Initialized successfully", "Game");
    }

    private static void SeedWorld(IEntityStore world)
    {
        // Model loading and entity creation is now handled dynamically.
    }

    private ulong _editorCameraEnt = 0;

    public event Action<ulong>? OnEntityPicked;
    /// <inheritdoc />
    public event Action<ViewportRendererMode>? RendererModeChanged;
    /// <inheritdoc />
    public event Action<ViewportProjectionMode>? ProjectionModeChanged;
    /// <inheritdoc />
    public event Action<ViewportDebugView>? DebugViewChanged;
    /// <inheritdoc />
    public event Action<ulong>? EntityTransformEditStarted;
    /// <inheritdoc />
    public event Action<ulong>? EntityTransformEditCompleted;
    /// <inheritdoc />
    public event Action<string>?
        RendererPluginEnableRequested;

    /// <inheritdoc />
    public ViewportGizmoOperation GizmoOperation { get; set; } =
        ViewportGizmoOperation.Translate;

    /// <inheritdoc />
    public ViewportGizmoSpace GizmoSpace { get; set; } =
        ViewportGizmoSpace.Local;

    /// <inheritdoc />
    public bool GizmoSnapping { get; set; }

    /// <inheritdoc />
    private bool _isPathTracingRendererAvailable;

    /// <inheritdoc />
    public bool IsPathTracingRendererAvailable
    {
        get => _isPathTracingRendererAvailable;
        set
        {
            if (value)
            {
                IRendererPlanPlugin? plugin =
                    _pluginRuntime
                        ?.LoadPathTracing();
                _isPathTracingRendererAvailable =
                    plugin != null;
                _renderer?.SetPathTracingPlugin(
                    plugin);
                return;
            }

            if (RendererMode ==
                ViewportRendererMode.PathTracing)
            {
                RendererMode =
                    ViewportRendererMode.Raster;
            }
            _renderer?.SetPathTracingPlugin(null);
            _pluginRuntime?.Unload(
                "core.renderer.path-tracing");
            _isPathTracingRendererAvailable = false;
        }
    }

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

    /// <inheritdoc />
    public ViewportRendererMode RendererMode
    {
        get => _renderer?.UsePathTracer == true
            ? ViewportRendererMode.PathTracing
            : ViewportRendererMode.Raster;
        set
        {
            if (_renderer == null || RendererMode == value)
                return;
            if (value ==
                    ViewportRendererMode.PathTracing &&
                !IsPathTracingRendererAvailable)
            {
                RendererPluginEnableRequested?.Invoke(
                    "core.renderer.path-tracing");
                return;
            }

            _renderer.UsePathTracer =
                value == ViewportRendererMode.PathTracing;
            RendererModeChanged?.Invoke(value);
        }
    }

    /// <inheritdoc />
    public ViewportProjectionMode ProjectionMode
    {
        get => _renderer?.ProjectionMode ??
            ViewportProjectionMode.Perspective;
        set
        {
            if (_renderer == null || _renderer.ProjectionMode == value)
                return;
            _renderer.ProjectionMode = value;
            ProjectionModeChanged?.Invoke(value);
        }
    }

    /// <inheritdoc />
    public ViewportDebugView DebugView
    {
        get => _renderer?.DebugView ?? ViewportDebugView.Lit;
        set
        {
            if (_renderer == null || _renderer.DebugView == value)
                return;
            _renderer.DebugView = value;
            DebugViewChanged?.Invoke(value);
        }
    }

    /// <inheritdoc />
    public float CameraFieldOfViewDegrees
    {
        get => _editorFieldOfViewDegrees;
        set
        {
            _editorFieldOfViewDegrees =
                Math.Clamp(value, 15.0f, 120.0f);
            if (_world != null &&
                _editorCameraEnt != 0 &&
                _world.TryGet<Camera>(
                    _editorCameraEnt,
                    out Camera camera))
            {
                camera.FieldOfView =
                    _editorFieldOfViewDegrees *
                    (MathF.PI / 180.0f);
                _world.Set(_editorCameraEnt, camera);
            }
        }
    }

    /// <inheritdoc />
    public float OrthographicSize
    {
        get => _renderer?.OrthographicSize ?? 20.0f;
        set
        {
            if (_renderer != null)
                _renderer.OrthographicSize =
                    Math.Clamp(value, 0.1f, 1000.0f);
        }
    }

    private void EnsureCamera()
    {
        if (_world == null) return;
        if (_editorCameraEnt != 0 &&
            _world.TryGet<Camera>(
                _editorCameraEnt,
                out _))
        {
            return;
        }
        _editorCameraEnt = 0;

        _editorCameraEnt = _world.CreateEntity();
        _world.Set(_editorCameraEnt, new Camera
        {
            FieldOfView =
                _editorFieldOfViewDegrees *
                (MathF.PI / 180.0f),
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
    private float _sceneAnimationTime;

    private void DrawEditorGizmo(
        InputState input,
        uint width,
        uint height)
    {
        _gizmoConsumesPointer = false;
        if (_world == null ||
            _renderer == null ||
            SelectedEntity == 0 ||
            SelectedEntity == _editorCameraEnt ||
            !_world.TryGet<Transform>(
                SelectedEntity,
                out Transform transform) ||
            !_world.TryGet<Camera>(
                _editorCameraEnt,
                out Camera camera) ||
            !_world.TryGet<Transform>(
                _editorCameraEnt,
                out Transform cameraTransform))
        {
            CompleteGizmoEdit();
            return;
        }

        float logicalWidth = input.LogicalWidth > 0.0f
            ? input.LogicalWidth
            : width;
        float logicalHeight = input.LogicalHeight > 0.0f
            ? input.LogicalHeight
            : height;
        float aspect =
            logicalWidth /
            MathF.Max(logicalHeight, 1.0f);
        ViewportCameraProjection.BuildMatrices(
            camera,
            cameraTransform,
            Vector3.UnitZ,
            aspect,
            _renderer.ProjectionBlend,
            _renderer.OrthographicSize,
            out Matrix4x4 view,
            out Matrix4x4 projection,
            out _);

        Matrix4x4 model =
            Matrix4x4.CreateScale(transform.Scale) *
            Matrix4x4.CreateFromQuaternion(
                transform.Rotation) *
            Matrix4x4.CreateTranslation(
                transform.Position);
        Matrix4x4 gizmoView = Matrix4x4.Transpose(view);
        Matrix4x4 gizmoProjection =
            Matrix4x4.Transpose(projection);
        Matrix4x4 gizmoModel =
            Matrix4x4.Transpose(model);
        Matrix4x4 delta = Matrix4x4.Identity;
        float snap = GizmoOperation switch
        {
            ViewportGizmoOperation.Rotate => 15.0f,
            ViewportGizmoOperation.Scale => 0.1f,
            _ => 0.5f
        };

        ImGuizmo.BeginFrame();
        ImGuizmo.SetDrawlist();
        ImGuizmo.SetRect(
            0.0f,
            0.0f,
            logicalWidth,
            logicalHeight);
        ImGuizmo.SetOrthographic(
            _renderer.ProjectionBlend >= 0.5f);
        ImGuizmo.SetGizmoSizeClipSpace(0.12f);
        bool changed = GizmoSnapping
            ? ImGuizmo.Manipulate(
                ref gizmoView.M11,
                ref gizmoProjection.M11,
                GetGizmoOperation(),
                GetGizmoMode(),
                ref gizmoModel.M11,
                ref delta.M11,
                ref snap)
            : ImGuizmo.Manipulate(
                ref gizmoView.M11,
                ref gizmoProjection.M11,
                GetGizmoOperation(),
                GetGizmoMode(),
                ref gizmoModel.M11);
        bool isUsing = ImGuizmo.IsUsing();
        _gizmoConsumesPointer =
            isUsing ||
            ImGuizmo.IsOver();

        if (isUsing && !_gizmoWasUsing)
        {
            _gizmoEntity = SelectedEntity;
            EntityTransformEditStarted?.Invoke(
                _gizmoEntity);
        }

        if (changed)
        {
            Matrix4x4 result =
                Matrix4x4.Transpose(gizmoModel);
            if (Matrix4x4.Decompose(
                    result,
                    out Vector3 scale,
                    out Quaternion rotation,
                    out Vector3 translation))
            {
                transform.Position = translation;
                transform.Rotation =
                    LightMath.SanitizeQuaternion(rotation);
                transform.Scale = new Vector3(
                    SanitizeGizmoScale(scale.X),
                    SanitizeGizmoScale(scale.Y),
                    SanitizeGizmoScale(scale.Z));
                _world.Set(
                    SelectedEntity,
                    transform);
                SyncLightDirectionFromTransform(
                    SelectedEntity,
                    transform);
            }
        }

        if (!isUsing && _gizmoWasUsing)
            CompleteGizmoEdit();
        _gizmoWasUsing = isUsing;
    }

    private ImGuizmoNET.OPERATION GetGizmoOperation()
        => GizmoOperation switch
        {
            ViewportGizmoOperation.Rotate =>
                ImGuizmoNET.OPERATION.ROTATE,
            ViewportGizmoOperation.Scale =>
                ImGuizmoNET.OPERATION.SCALE,
            _ => ImGuizmoNET.OPERATION.TRANSLATE
        };

    private ImGuizmoNET.MODE GetGizmoMode()
        => GizmoSpace == ViewportGizmoSpace.World
            ? ImGuizmoNET.MODE.WORLD
            : ImGuizmoNET.MODE.LOCAL;

    private static float SanitizeGizmoScale(float value)
        => float.IsFinite(value) &&
           MathF.Abs(value) >= 0.0001f
            ? value
            : 1.0f;

    private void SyncLightDirectionFromTransform(
        ulong entity,
        Transform transform)
    {
        if (_world == null)
            return;

        Vector3 direction =
            LightMath.GetSpotDirection(
                transform.Rotation);
        if (_world.TryGet<SpotLightComponent>(
                entity,
                out SpotLightComponent spot))
        {
            spot.Direction = direction;
            _world.Set(entity, spot);
        }
        if (_world.TryGet<DirectionalLightComponent>(
                entity,
                out DirectionalLightComponent directional))
        {
            directional.Direction = direction;
            _world.Set(entity, directional);
        }
    }

    private void CompleteGizmoEdit()
    {
        if (_gizmoWasUsing && _gizmoEntity != 0)
        {
            EntityTransformEditCompleted?.Invoke(
                _gizmoEntity);
        }
        _gizmoWasUsing = false;
        _gizmoEntity = 0;
    }

    public void Update(InputState input)
    {
        if (_world == null) return;
        EnsureCamera();
        _renderer?.UpdateProjectionTransition(input.DeltaTime);
        UpdateOrbitingLights(input.DeltaTime);

        // Toggle between path tracer and rasterizer with P key
        if (input.KeyP && !_wasKeyPDown)
        {
            if (RendererMode ==
                    ViewportRendererMode.Raster &&
                !IsPathTracingRendererAvailable)
            {
                RendererPluginEnableRequested?.Invoke(
                    "core.renderer.path-tracing");
            }
            else
            {
                RendererMode =
                    RendererMode ==
                        ViewportRendererMode.Raster
                        ? ViewportRendererMode.PathTracing
                        : ViewportRendererMode.Raster;
            }
            var mode = RendererMode == ViewportRendererMode.PathTracing
                ? "Path Tracer"
                : "Rasterizer (PBR)";
            Info($"[GameLoop] Switched to {mode}", "Game");
        }
        _wasKeyPDown = input.KeyP;

        if (_imguiRenderer != null)
        {
            _imguiRenderer.BeginFrame(input, _lastWidth, _lastHeight, input.Events, ref _imGuiFrameStarted);
        }

        if (input.MouseDownLeft &&
            !_wasMouseDownLeft &&
            !_gizmoConsumesPointer &&
            _renderer != null &&
            _lastWidth > 0 &&
            _lastHeight > 0)
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
        _sceneAnimationTime = 0.0f;
        _pluginRuntime?.SetProjectRoot(contentRoot);
        _imguiRenderer?.LoadShaders(contentRoot);
        _renderer?.LoadScene(contentRoot, sceneName);
        // Re-seed AFTER scene load so game-code edits always override scene defaults.
        // This is what makes hot-reload vertex/color edits take effect:
        // the scene JSON provides fallback geometry, but SeedWorld has final say.
        if (_world is not null)
            SeedWorld(_world);
    }

    private void UpdateOrbitingLights(float deltaTime)
    {
        if (_world == null)
            return;

        _sceneAnimationTime += Math.Clamp(deltaTime, 0.0f, 0.1f);
        foreach (ulong entity in _world.Entities)
        {
            if (!_world.TryGet<OrbitingLightComponent>(
                    entity,
                    out var orbit) ||
                !_world.TryGet<Transform>(
                    entity,
                    out var transform))
            {
                continue;
            }

            transform.Position =
                ProceduralDemoSceneBuilder.EvaluateOrbit(
                    orbit,
                    _sceneAnimationTime);
            if (orbit.AimAtCenter)
            {
                Vector3 direction = Vector3.Normalize(
                    orbit.Center - transform.Position);
                transform.Rotation =
                    Engine.Scene.LightMath.GetSpotRotation(direction);
                if (_world.TryGet<SpotLightComponent>(
                        entity,
                        out var spotLight))
                {
                    spotLight.Direction = direction;
                    _world.Set(entity, spotLight);
                }
            }
            _world.Set(entity, transform);
        }
    }

    public ulong AddPointLight(Vector3 position, Vector3 color, float intensity, float range, float sourceRadius, bool castShadows = true)
    {
        if (_renderer == null) return 0;
        return _renderer.AddPointLight(position, color, intensity, range, sourceRadius, castShadows);
    }

    public ulong AddDirectionalLight(Vector3 direction, Vector3 color, float intensity, float angularRadius, bool castShadows = true)
    {
        if (_renderer == null) return 0;
        return _renderer.AddDirectionalLight(
            direction,
            color,
            intensity,
            angularRadius,
            castShadows);
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

    public RenderGraphDiagnosticsSnapshot? GetRenderGraphDiagnostics()
        => _renderer?.GetRenderGraphDiagnostics();

    public bool RenderShadowAtlasTilePreview(
        ulong entityId,
        int faceIndex,
        bool dynamicTile,
        RhiTexture target,
        uint width = 512,
        uint height = 512,
        RhiFence? syncFence = null,
        ulong waitValue = 0,
        ulong signalValue = 0)
        => _renderer?.RenderShadowAtlasTilePreview(
            entityId,
            faceIndex,
            dynamicTile,
            target,
            width,
            height,
            syncFence,
            waitValue,
            signalValue) ?? false;

    public void RenderThumbnail(string contentRoot, string assetPath, string assetType, RhiTexture target, uint width = 256, uint height = 256, float orbitRadians = 0.0f, int modelPartIndex = -1, RhiFence? syncFence = null, ulong waitValue = 0, ulong signalValue = 0)
    {
        if (_device == null) return;

        var tempWorld = new EcsWorld();
        ulong camEnt = tempWorld.CreateEntity();
        float thumbnailAspect = height > 0 ? width / (float)height : 1.0f;
        tempWorld.Set(camEnt, GetPreviewCamera(0.5f, 3.0f));
        tempWorld.Set(camEnt, new Transform { Position = new Vector3(0, 0, -3.0f), Rotation = Quaternion.Identity });

        if (assetType == "Model")
        {
            var model = Engine.Assets.ModelLoader.LoadMdl(_device, assetPath);
            if (modelPartIndex >= 0)
            {
                model = Engine.Assets.ModelLoader.SelectPart(
                    model,
                    modelPartIndex);
            }
            ulong modelId = Engine.Assets.AssetRegistry.RegisterModel(model);

            (Vector3 center, float radius) =
                Engine.Assets.ModelLoader.GetBoundingSphere(model);
            float distance = GetSpherePreviewDistance(
                radius,
                thumbnailAspect,
                ModelPreviewViewportFill);

            Quaternion previewRotation = Quaternion.CreateFromYawPitchRoll(ModelPreviewYaw + orbitRadians, ModelPreviewPitch, 0f);
            Vector3 previewPosition =
                GetPreviewPivotPosition(center, previewRotation);

            ulong ent = tempWorld.CreateEntity();
            tempWorld.Set(ent, Engine.RHI.ModelComponent.Create(modelId));
            tempWorld.Set(ent, new Transform { Position = previewPosition, Scale = Vector3.One, Rotation = previewRotation });
            tempWorld.Set(
                camEnt,
                GetPreviewCameraTransform(distance));
            tempWorld.Set(
                camEnt,
                GetPreviewCamera(radius, distance));
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
            float materialDistance = GetSpherePreviewDistance(
                1.0f,
                thumbnailAspect,
                MaterialPreviewViewportFill);
            tempWorld.Set(
                camEnt,
                GetPreviewCameraTransform(materialDistance));
            tempWorld.Set(
                camEnt,
                GetPreviewCamera(1.0f, materialDistance));
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

    public void LoadModelPreview(
        string contentRoot,
        string modelPath,
        int modelPartIndex = -1)
    {
        if (_device == null || _world == null || _renderer == null) return;
        _world.Clear();
        _previewAssetType = "Model";
        _previewMatId = 0;
        _previewEntity = 0;
        _previewCenter = Vector3.Zero;
        _previewDistance = 3.0f;

        ulong camEnt = _world.CreateEntity();
        _world.Set(camEnt, GetPreviewCamera(0.5f, 3.0f));
        _world.Set(camEnt, new Transform { Position = new Vector3(0, 0, -4.0f), Rotation = Quaternion.Identity });
        _renderer.ActiveCameraEntity = camEnt;
        _previewCameraEntity = camEnt;

        var model = Engine.Assets.ModelLoader.LoadMdl(_device, modelPath);
        if (modelPartIndex >= 0)
        {
            model = Engine.Assets.ModelLoader.SelectPart(
                model,
                modelPartIndex);
        }
        ulong modelId = Engine.Assets.AssetRegistry.RegisterModel(model);

        (Vector3 center, float radius) =
            Engine.Assets.ModelLoader.GetBoundingSphere(model);
        _previewCenter = center;

        _previewDistance =
            GetSpherePreviewDistance(
                radius,
                1.0f,
                ModelPreviewViewportFill);
        _world.Set(
            camEnt,
            GetPreviewCamera(radius, _previewDistance));

        Quaternion previewRotation = Quaternion.CreateFromYawPitchRoll(ModelPreviewYaw, ModelPreviewPitch, 0f);
        Vector3 previewPosition =
            GetPreviewPivotPosition(center, previewRotation);

        ulong ent = _world.CreateEntity();
        _previewEntity = ent;
        _world.Set(ent, Engine.RHI.ModelComponent.Create(modelId));
        _world.Set(ent, new Transform
        {
            Position = previewPosition,
            Rotation = previewRotation,
            Scale = Vector3.One
        });

        _world.Set(
            camEnt,
            GetPreviewCameraTransform(_previewDistance));
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
        _previewDistance =
            GetSpherePreviewDistance(
                1.0f,
                1.0f,
                MaterialPreviewViewportFill);

        ulong camEnt = _world.CreateEntity();
        _world.Set(
            camEnt,
            GetPreviewCamera(1.0f, _previewDistance));
        _world.Set(
            camEnt,
            GetPreviewCameraTransform(_previewDistance));
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
                ? GetPreviewPivotPosition(_previewCenter, rotation)
                : Vector3.Zero;
            _world.Set(_previewEntity, previewTransform);
        }

        if (_world.TryGet<Transform>(_previewCameraEntity, out var cameraTransform))
        {
            Transform targetCamera =
                GetPreviewCameraTransform(_previewDistance);
            cameraTransform.Position = targetCamera.Position;
            cameraTransform.Rotation = targetCamera.Rotation;
            cameraTransform.Scale = targetCamera.Scale;
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

    /// <inheritdoc />
    public void ReloadPluginShaders(string pluginId)
    {
        _renderer?.ReloadPluginShaders(pluginId);
    }

    /// <inheritdoc />
    public void ReloadPluginCode(string pluginId)
    {
        if (pluginId ==
            "core.renderer.clustered")
        {
            _renderer?.SetClusteredPlugin(null);
            _pluginRuntime?.Unload(pluginId);
            IRendererPlanPlugin? clustered =
                _pluginRuntime?.LoadClustered();
            if (clustered == null)
            {
                throw new InvalidOperationException(
                    "Required clustered renderer plugin reload failed.");
            }
            _renderer?.SetClusteredPlugin(
                clustered);
            return;
        }
        if (pluginId !=
            "core.renderer.path-tracing")
            return;

        bool reactivate =
            RendererMode ==
            ViewportRendererMode.PathTracing;
        if (reactivate)
            RendererMode =
                ViewportRendererMode.Raster;
        _renderer?.SetPathTracingPlugin(null);
        _pluginRuntime?.Unload(pluginId);
        IRendererPlanPlugin? plugin =
            _pluginRuntime?.LoadPathTracing();
        _renderer?.SetPathTracingPlugin(plugin);
        _isPathTracingRendererAvailable =
            plugin != null;
        if (reactivate && plugin != null)
            RendererMode =
                ViewportRendererMode.PathTracing;
    }

    public void Dispose()
    {
        _renderer?.Dispose();
        _renderer = null;
        _imguiRenderer?.Dispose();
        _imguiRenderer = null;
        _pluginRuntime?.Dispose();
        _pluginRuntime = null;
        _swap?.Dispose();
        _swap = null;
        _device?.Dispose();
        _device = null;
    }
}
