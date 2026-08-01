// SPDX-License-Identifier: MIT
using System;
using Engine.CBindings;
using Engine.RHI;
using static Engine.CBindings.Log;
using System.Numerics;
using Engine.Scene;
using Engine.Scene.Components;
using Engine.Renderer;
using Camera = Engine.Scene.Components.Camera;
using ImGuizmoNET;

namespace Engine.Game;

public sealed class GameLoop : IGameLoop
{
    private RhiDevice? _device;
    private RhiSwapchain? _swap;
    private IEntityStore? _world;
    private GameRenderer? _gameRenderer;
    private uint _lastWidth = 1280;
    private uint _lastHeight = 720;
    private bool _enableImGui;

    public GameLoop()
    {
        _enableImGui = true;
    }

    public GameLoop(bool enableImGui)
    {
        _enableImGui = enableImGui;
    }

    public event Action<ulong>? OnEntityPicked;
    public Action<InputState, uint, uint>? DrawPluginOverlay { get; set; }
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
    public event Action<string>? RendererPluginEnableRequested;

    public void Init(IntPtr deviceHandle, IntPtr swapchainHandle, IEntityStore world, bool enableImGui = true)
    {
        Info("[GameLoop] Initializing...", "Game");
        _device = new RhiDevice(deviceHandle, ownsHandle: false);
        _swap = new RhiSwapchain(_device, swapchainHandle, ownsHandle: false);
        _world = world;
        _enableImGui = enableImGui;

        _gameRenderer = new GameRenderer(_device, _swap, _world, enableImGui);
        _gameRenderer.OnEntityPicked += id => OnEntityPicked?.Invoke(id);
        _gameRenderer.RendererModeChanged += mode => RendererModeChanged?.Invoke(mode);
        _gameRenderer.ProjectionModeChanged += mode => ProjectionModeChanged?.Invoke(mode);
        _gameRenderer.DebugViewChanged += view => DebugViewChanged?.Invoke(view);
        _gameRenderer.EntityTransformEditStarted += id => EntityTransformEditStarted?.Invoke(id);
        _gameRenderer.EntityTransformEditCompleted += id => EntityTransformEditCompleted?.Invoke(id);
        _gameRenderer.RendererPluginEnableRequested += id => RendererPluginEnableRequested?.Invoke(id);
        _gameRenderer.DrawPluginOverlayProvider = () => DrawPluginOverlay;
        Info("[GameLoop] Initialized successfully", "Game");
    }

    /// <inheritdoc />
    public ViewportGizmoOperation GizmoOperation
    {
        get => _gameRenderer?.GizmoOperation ?? ViewportGizmoOperation.Translate;
        set { if (_gameRenderer != null) _gameRenderer.GizmoOperation = value; }
    }

    /// <inheritdoc />
    public ViewportGizmoSpace GizmoSpace
    {
        get => _gameRenderer?.GizmoSpace ?? ViewportGizmoSpace.Local;
        set { if (_gameRenderer != null) _gameRenderer.GizmoSpace = value; }
    }

    /// <inheritdoc />
    public bool GizmoSnapping
    {
        get => _gameRenderer?.GizmoSnapping ?? false;
        set { if (_gameRenderer != null) _gameRenderer.GizmoSnapping = value; }
    }

    /// <inheritdoc />
    public bool IsPathTracingRendererAvailable
    {
        get => _gameRenderer?.IsPathTracingRendererAvailable ?? false;
        set { if (_gameRenderer != null) _gameRenderer.IsPathTracingRendererAvailable = value; }
    }

    /// <inheritdoc />
    public bool HasPendingRenderWork =>
        _gameRenderer?.HasPendingRenderWork ?? false;

    public ulong SelectedEntity
    {
        get => _gameRenderer?.SelectedEntity ?? 0;
        set { if (_gameRenderer != null) _gameRenderer.SelectedEntity = value; }
    }

    public void SetSelectedEntity(ulong entityId) => SelectedEntity = entityId;

    /// <inheritdoc />
    public ViewportRendererMode RendererMode
    {
        get => _gameRenderer?.RendererMode ?? ViewportRendererMode.Raster;
        set { if (_gameRenderer != null) _gameRenderer.RendererMode = value; }
    }

    /// <inheritdoc />
    public ViewportProjectionMode ProjectionMode
    {
        get => _gameRenderer?.ProjectionMode ?? ViewportProjectionMode.Perspective;
        set { if (_gameRenderer != null) _gameRenderer.ProjectionMode = value; }
    }

    /// <inheritdoc />
    public ViewportDebugView DebugView
    {
        get => _gameRenderer?.DebugView ?? ViewportDebugView.Lit;
        set { if (_gameRenderer != null) _gameRenderer.DebugView = value; }
    }

    /// <inheritdoc />
    public float CameraFieldOfViewDegrees
    {
        get => _gameRenderer?.CameraFieldOfViewDegrees ?? 60.0f;
        set { if (_gameRenderer != null) _gameRenderer.CameraFieldOfViewDegrees = value; }
    }

    /// <inheritdoc />
    public float OrthographicSize
    {
        get => _gameRenderer?.OrthographicSize ?? 20.0f;
        set { if (_gameRenderer != null) _gameRenderer.OrthographicSize = value; }
    }

    public void Update(InputState input)
    {
        _gameRenderer?.Update(input, _lastWidth, _lastHeight);
    }

    public void LoadScene(string contentRoot, string sceneName)
    {
        _gameRenderer?.LoadScene(contentRoot, sceneName);
        BuildProceduralDemoIfPresent(contentRoot);
    }

    /// <summary>If the freshly loaded scene carries a
    /// <c>ProceduralDemoDefinition</c> with <c>Enabled == true</c>,
    /// expand it into world entities via the procedural-demo builder.
    /// The builder is invoked AFTER <see cref="GameRenderer.LoadScene"/>
    /// so the procedural entities are added on top of the explicit
    /// <c>models[]</c> list (this scene type uses <c>"models": []</c>
    /// and relies entirely on the procedural builder to populate
    /// geometry + lights). Marked private because every other
    /// load-site should route through <see cref="LoadScene"/>.</summary>
    private void BuildProceduralDemoIfPresent(string contentRoot)
    {
        if (_device == null || _world == null) return;
        SceneGraph? scene = _gameRenderer?.CurrentScene;
        ProceduralDemoDefinition? definition = scene?.ProceduralDemo;
        if (definition == null || !definition.Enabled) return;

        Engine.Game.ProceduralDemoSceneBuilder.Build(
            _device, _world, contentRoot, definition);
    }

    public ulong AddPointLight(Vector3 position, Vector3 color, float intensity, float range, float sourceRadius, bool castShadows = true)
        => _gameRenderer?.AddPointLight(position, color, intensity, range, sourceRadius, castShadows) ?? 0;

    public ulong AddDirectionalLight(Vector3 direction, Vector3 color, float intensity, float angularRadius, bool castShadows = true)
        => _gameRenderer?.AddDirectionalLight(direction, color, intensity, angularRadius, castShadows) ?? 0;

    public ulong AddSpotLight(Vector3 position, Vector3 direction, Vector3 color, float intensity, float range, float innerCone, float outerCone, float sourceRadius, bool castShadows = true)
        => _gameRenderer?.AddSpotLight(position, direction, color, intensity, range, innerCone, outerCone, sourceRadius, castShadows) ?? 0;

    public void ReplaceSwapchain(RhiSwapchain swapchain)
    {
        var oldSwap = _swap;
        _swap = swapchain;
        _gameRenderer?.ReplaceSwapchain(swapchain);
        oldSwap?.Dispose();
    }

    public void RenderFrame(RhiTexture backBuffer, uint width, uint height)
    {
        _lastWidth = width;
        _lastHeight = height;
        _gameRenderer?.RenderFrame(backBuffer, width, height);
    }

    public RenderGraphDiagnosticsSnapshot? GetRenderGraphDiagnostics()
        => _gameRenderer?.GetRenderGraphDiagnostics();

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
        => _gameRenderer?.RenderShadowAtlasTilePreview(
            entityId, faceIndex, dynamicTile, target,
            width, height, syncFence, waitValue, signalValue) ?? false;

    public void RenderThumbnail(string contentRoot, string assetPath, string assetType, RhiTexture target, uint width = 256, uint height = 256, float orbitRadians = 0.0f, int modelPartIndex = -1, RhiFence? syncFence = null, ulong waitValue = 0, ulong signalValue = 0)
        => _gameRenderer?.RenderThumbnail(contentRoot, assetPath, assetType, target, width, height, orbitRadians, modelPartIndex, syncFence, waitValue, signalValue);

    public void LoadModelPreview(string contentRoot, string modelPath, int modelPartIndex = -1)
        => _gameRenderer?.LoadModelPreview(contentRoot, modelPath, modelPartIndex);

    public void LoadMaterialPreview(string contentRoot, string materialPath, bool usePathTracer = true)
        => _gameRenderer?.LoadMaterialPreview(contentRoot, materialPath, usePathTracer);

    public void RenderLoadedPreview(RhiTexture target, uint width = 256, uint height = 256, float orbitRadians = 0.0f, RhiFence? syncFence = null, ulong waitValue = 0, ulong signalValue = 0)
        => _gameRenderer?.RenderLoadedPreview(target, width, height, orbitRadians, syncFence, waitValue, signalValue);

    public void UpdateMaterialPreview(float[] albedo, float metallic, float roughness, float subsurface, float[] subsurfaceColor, float[] subsurfaceRadius, float clearcoat, float clearcoatRoughness, float[] topColor, float topMetallic, float topRoughness, uint topMaskType, float noiseScale = 10.0f, float noiseThresholdMin = 0.3f, float noiseThresholdMax = 0.7f, float[]? layer2Color = null, float layer2Metallic = 0.0f, float layer2Roughness = 1.0f, uint layer2MaskType = 0, float layer2NoiseScale = 10.0f, float layer2NoiseMin = 0.3f, float layer2NoiseMax = 0.7f)
        => _gameRenderer?.UpdateMaterialPreview(albedo, metallic, roughness, subsurface, subsurfaceColor, subsurfaceRadius, clearcoat, clearcoatRoughness, topColor, topMetallic, topRoughness, topMaskType, noiseScale, noiseThresholdMin, noiseThresholdMax, layer2Color, layer2Metallic, layer2Roughness, layer2MaskType, layer2NoiseScale, layer2NoiseMin, layer2NoiseMax);

    public void ApplyMaterialToSubmesh(uint x, uint y, uint w, uint h, string materialPath)
        => _gameRenderer?.ApplyMaterialToSubmesh(x, y, w, h, materialPath);

    /// <inheritdoc />
    public void InvalidateRenderPlan()
        => _gameRenderer?.InvalidateRenderPlan();

    public void ReloadPluginShaders(string pluginId)
        => _gameRenderer?.ReloadPluginShaders(pluginId);

    /// <inheritdoc />
    public void ReloadPluginCode(string pluginId)
        => _gameRenderer?.ReloadPluginCode(pluginId);

    public void Dispose()
    {
        _gameRenderer?.Dispose();
        _gameRenderer = null;
        _swap?.Dispose();
        _swap = null;
        _device?.Dispose();
        _device = null;
    }
}
