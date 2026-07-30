// SPDX-License-Identifier: MIT
// Renderer orchestration: takes a Scene, builds the pass list, compiles the
// render graph, drives the executor each frame.
//
// World ownership: the renderer does NOT own the EcsWorld; the caller hands
// it in at construction. This keeps the renderer free of GC pressure and
// lets the editor share a single world across multiple viewports later.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.IO;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.Scene;
using Engine.Scene.Components;
using Engine.Assets;
using static Engine.CBindings.Log;

namespace Engine.Game;

public sealed class Renderer : IDisposable
{
    private sealed class CachedRenderPlan : IDisposable
    {
        public required RenderPlan Plan { get; init; }
        public RasterSceneGpuCache? RasterSceneCache { get; init; }
        public DirectionalShadowState? DirectionalShadowState { get; init; }
        public DirectionalShadowPass? DirectionalShadowPass { get; init; }
        public PunctualShadowState? PunctualShadowState { get; init; }
        public PunctualShadowPass? PunctualShadowPass { get; init; }

        public void Dispose()
        {
            Plan.Passes.DisposeAll();
            RasterSceneCache?.Dispose();
        }
    }

    private readonly RhiDevice _device;
    private readonly RhiSwapchain _swap;
    private readonly IEntityStore _world;
    private SceneLoader? _loader;
    private string _contentRoot = "Content";
    private readonly ImGuiRenderer? _imguiRenderer;

    private RenderPlan? _plan;
    private SceneGraph? _currentScene;
    private readonly RenderGraphExecutor _graphExecutor;
    private readonly GpuWorkScheduler _gpuWorkScheduler = new();
    private long _renderedFrameCount;
    private long _renderPlanVersion;
    private CachedRenderPlan? _rasterPlan;
    private CachedRenderPlan? _pathTracingPlan;

    /// <summary>Sentinel handle on which the executor binds the swapchain
    /// back-buffer before each frame. Console-friendly constant so callers
    /// can reference the same handle.</summary>
    public static readonly ResourceHandle BackBufferHandle = new(0x80000000);
    public static readonly ResourceHandle DepthBufferHandle = new(0x80000001);
    public static readonly ResourceHandle OutlineMaskHandle = new(0x80000002);
    private const uint DirectionalShadowMapHandleBase = 0x80000003;

    public static ResourceHandle GetDirectionalShadowMapHandle(int cascadeIndex)
        => GetShadowPageHandle(cascadeIndex);

    public static ResourceHandle GetShadowPageHandle(int pageIndex)
        => new(DirectionalShadowMapHandleBase + (uint)pageIndex);

    private string _lastSceneName = "";
    private bool _usePathTracer;
    private bool _renderSky = true;
    private bool _renderGrid = true;
    private bool _renderShadows = true;
    private RhiBindlessHeap _sharedBindlessHeap;
    private RasterSceneGpuCache? _rasterSceneCache;
    private DirectionalShadowState? _directionalShadowState;
    private DirectionalShadowPass? _directionalShadowPass;
    private PunctualShadowState? _punctualShadowState;
    private PunctualShadowPass? _punctualShadowPass;
    private ShadowAtlasPreviewRenderer? _shadowAtlasPreviewRenderer;
    private long _lastConsumedGpuTimingFrame = -1;
    private ViewportProjectionMode _projectionMode;
    private float _projectionBlend;
    private ViewportDebugView _debugView;
    private IRendererPlanPlugin? _clusteredPlugin;
    private IRendererPlanPlugin? _pathTracingPlugin;

    private RhiTexture? _depthTexture;
    private RhiTexture? _outlineMaskTexture;
    private uint _depthWidth, _depthHeight;

    private ulong _selectedEntity;
    public ulong SelectedEntity
    {
        get => _selectedEntity;
        set { _selectedEntity = value; }
    }

    public ulong ActiveCameraEntity { get; set; }
    public float OrthographicSize { get; set; } = 20.0f;
    internal float ProjectionBlend => _projectionBlend;

    public ViewportProjectionMode ProjectionMode
    {
        get => _projectionMode;
        set => _projectionMode = value;
    }

    internal void UpdateProjectionTransition(float deltaTime)
    {
        float target = ProjectionMode ==
            ViewportProjectionMode.Orthographic
                ? 1.0f
                : 0.0f;
        float step = MathF.Max(0.0f, deltaTime) * 5.0f;
        _projectionBlend = target > _projectionBlend
            ? MathF.Min(target, _projectionBlend + step)
            : MathF.Max(target, _projectionBlend - step);
    }

    public ViewportDebugView DebugView
    {
        get => _debugView;
        set => _debugView = value;
    }

    internal uint DebugFlags => (uint)_debugView;

    internal CameraData BuildCameraData(
        Engine.Scene.Components.Camera camera,
        Transform transform,
        float aspect,
        Vector3 localForward)
        => ViewportCameraProjection.Build(
            camera,
            transform,
            localForward,
            aspect,
            _projectionBlend,
            OrthographicSize);


    public bool UsePathTracer
    {
        get => _usePathTracer;
        set
        {
            if (_usePathTracer != value)
            {
                _usePathTracer = value;
                if (_currentScene != null)
                    ActivateOrCompileRenderPlan(
                        _currentScene,
                        _contentRoot);
            }
        }
    }

    public Renderer(
        RhiDevice device,
        RhiSwapchain swap,
        IEntityStore world,
        ImGuiRenderer? imguiRenderer = null,
        IRendererPlanPlugin? clusteredPlugin = null,
        IRendererPlanPlugin? pathTracingPlugin = null)
    {
        _device = device;
        _swap = swap;
        _world = world;
        _imguiRenderer = imguiRenderer;
        _clusteredPlugin = clusteredPlugin;
        _pathTracingPlugin = pathTracingPlugin;
        _sharedBindlessHeap = new RhiBindlessHeap(_device, 4096);
        _graphExecutor = new RenderGraphExecutor(_device)
        {
            EnableGpuTiming = true,
        };
    }

    public IEntityStore World => _world;

    public void SetPathTracingPlugin(
        IRendererPlanPlugin? plugin)
    {
        if (ReferenceEquals(
                _pathTracingPlugin,
                plugin))
        {
            return;
        }

        _pathTracingPlan?.Dispose();
        if (_plan == _pathTracingPlan?.Plan)
            _plan = null;
        _pathTracingPlan = null;
        _pathTracingPlugin = plugin;
        if (_usePathTracer &&
            plugin == null)
        {
            _usePathTracer = false;
            if (_currentScene != null)
            {
                ActivateOrCompileRenderPlan(
                    _currentScene,
                    _contentRoot);
            }
        }
    }

    public void SetClusteredPlugin(
        IRendererPlanPlugin? plugin)
    {
        if (ReferenceEquals(
                _clusteredPlugin,
                plugin))
        {
            return;
        }

        bool wasActive =
            _plan == _rasterPlan?.Plan;
        _rasterPlan?.Dispose();
        if (wasActive)
        {
            _plan = null;
            _rasterSceneCache = null;
            _directionalShadowState = null;
            _directionalShadowPass = null;
            _punctualShadowState = null;
            _punctualShadowPass = null;
        }
        _rasterPlan = null;
        _clusteredPlugin = plugin;
        if (!_usePathTracer &&
            plugin != null &&
            _currentScene != null)
        {
            ActivateOrCompileRenderPlan(
                _currentScene,
                _contentRoot);
        }
    }

    public void LoadScene(string contentRoot, string sceneName)
    {
        _contentRoot = contentRoot;
        _lastSceneName = sceneName;
        _loader = new SceneLoader(contentRoot);
        SceneGraph scene = _loader.Load(sceneName);

        _world.Clear();
        MeshLoader.ClearCache();
        MaterialLoader.ClearCache();
        TextureLoader.ClearCache();
        AssetRegistry.Clear();

        foreach (var modelRef in scene.Models)
        {
            var mdlPath = Path.Combine(_contentRoot, modelRef.Source);
            if (!File.Exists(mdlPath))
                mdlPath = Path.Combine(_contentRoot, "assets", Path.GetFileName(modelRef.Source));

            Model? model = null;
            try
            {
                model = Engine.Assets.ModelLoader.LoadMdl(_device, mdlPath);
                if (modelRef.PartIndex is int partIndex)
                {
                    model = Engine.Assets.ModelLoader.SelectPart(
                        model,
                        partIndex);
                }
            }
            catch (Exception ex)
            {
                Error($"[Renderer] Failed to load model '{mdlPath}': {ex.Message}", "Renderer");
                continue;
            }

            // Register all meshes and materials in the model parts
            for (int i = 0; i < model.Parts.Length; i++)
            {
                if (model.Parts[i].Mesh != null)
                {
                    ulong meshId = Engine.Assets.AssetRegistry.RegisterMesh(model.Parts[i].Mesh);
                }
            }

            ulong modelId = Engine.Assets.AssetRegistry.RegisterModel(model);

            ulong ent = _world.CreateEntity();
            _world.Set(
                ent,
                ModelComponent.Create(
                    modelId,
                    modelRef.StaticShadowCaster));

            var pos = modelRef.Position ?? new float[] { 0, 0, 0 };
            var rot = modelRef.Rotation ?? new float[] { 0, 0, 0, 1 };
            var scl = modelRef.Scale ?? new float[] { 1, 1, 1 };

            Quaternion q = Quaternion.Identity;
            if (rot.Length >= 4)
                q = new Quaternion(rot[0], rot[1], rot[2], rot[3]);
            else if (rot.Length == 3)
                q = Quaternion.CreateFromYawPitchRoll(rot[1] * MathF.PI / 180f, rot[0] * MathF.PI / 180f, rot[2] * MathF.PI / 180f);

            _world.Set(ent, new Engine.Scene.Components.Transform
            {
                Position = pos.Length >= 3 ? new Vector3(pos[0], pos[1], pos[2]) : Vector3.Zero,
                Rotation = q,
                Scale = scl.Length >= 3 ? new Vector3(scl[0], scl[1], scl[2]) : Vector3.One
            });
        }

        if (scene.ProceduralDemo is { Enabled: true } proceduralDemo)
        {
            ProceduralDemoSceneBuilder.Build(
                _device,
                _world,
                contentRoot,
                proceduralDemo);
            _usePathTracer = false;
        }

        foreach (var light in scene.Lights)
        {
            CreateLightEntity(light);
        }

        if (scene.Lights.Count == 0)
        {
            var defaultSun = new LightNode
            {
                Type = "directional",
                Direction = new[] { -0.4f, -1.0f, -0.35f },
                Color = new[] { 1.0f, 0.96f, 0.9f },
                Intensity = 3.5f,
                SunRadius = 0.012f,
                CastShadows = true
            };
            scene.Lights.Add(defaultSun);
            CreateLightEntity(defaultSun);
        }

        _currentScene = scene;
        _renderSky = true;
        _renderGrid = true;
        _renderShadows = true;
        RebuildRenderPlan(scene, contentRoot);
    }

    public void BuildThumbnailPlan(string contentRoot)
    {
        var sg = new SceneGraph();
        sg.Passes.Add(new ScenePass { ClearColor = new float[] { 0.15f, 0.15f, 0.15f, 1.0f } });

        sg.Lights.Add(new LightNode { Type = "directional", Color = new float[] { 1.0f, 0.94f, 0.9f }, Intensity = 3.6f, Position = new float[] { 2, 2, 2 }, Direction = new float[] { -0.8f, 1.0f, -0.6f }, SunRadius = 0.01f });
        sg.Lights.Add(new LightNode { Type = "directional", Color = new float[] { 0.72f, 0.82f, 1.0f }, Intensity = 1.2f, Position = new float[] { -2, 1, 2 }, Direction = new float[] { 0.65f, 0.55f, -0.75f }, SunRadius = 0.01f });
        sg.Lights.Add(new LightNode { Type = "directional", Color = new float[] { 1.0f, 1.0f, 1.0f }, Intensity = 0.8f, Position = new float[] { 0, 2, -3 }, Direction = new float[] { 0.15f, 0.9f, 0.4f }, SunRadius = 0.01f });

        _currentScene = sg;
        _usePathTracer = false;
        _renderSky = false;
        _renderGrid = false;
        _renderShadows = false;
        RebuildRenderPlan(sg, contentRoot);
    }

    public void BuildTextureThumbnailPlan(string contentRoot, RhiTexture sourceTexture)
    {
        _currentScene = null;
        _renderSky = false;
        _renderGrid = false;
        _renderShadows = false;

        var passes = new List<RenderPass>
        {
            new TextureThumbnailPass(_device, sourceTexture, contentRoot)
        };

        DisposeRenderPlans();
        _plan = new RenderGraphCompiler().Compile(passes);
        _renderPlanVersion++;
        _rasterSceneCache = null;
        _directionalShadowState = null;
        _directionalShadowPass = null;
        _punctualShadowState = null;
        _punctualShadowPass = null;
    }

    public ulong AddPointLight(Vector3 position, Vector3 color, float intensity, float range, float sourceRadius, bool castShadows = true)
    {
        var light = new LightNode
        {
            Type = "point",
            Position = new[] { position.X, position.Y, position.Z },
            Direction = new[] { 0f, -1f, 0f },
            Color = new[] { color.X, color.Y, color.Z },
            Intensity = intensity,
            Range = range,
            SourceRadius = sourceRadius,
            CastShadows = castShadows
        };
        _currentScene?.Lights.Add(light);
        return CreateLightEntity(light);
    }

    public ulong AddDirectionalLight(Vector3 direction, Vector3 color, float intensity, float angularRadius, bool castShadows = true)
    {
        Vector3 normalizedDirection = NormalizeOrFallback(
            direction,
            new Vector3(-0.4f, -1.0f, -0.35f));
        var light = new LightNode
        {
            Type = "directional",
            Position = new[] { 0.0f, 0.0f, 0.0f },
            Direction = new[]
            {
                normalizedDirection.X,
                normalizedDirection.Y,
                normalizedDirection.Z
            },
            Color = new[] { color.X, color.Y, color.Z },
            Intensity = intensity,
            SunRadius = angularRadius,
            CastShadows = castShadows
        };
        _currentScene?.Lights.Add(light);
        return CreateLightEntity(light);
    }

    public ulong AddSpotLight(Vector3 position, Vector3 direction, Vector3 color, float intensity, float range, float innerCone, float outerCone, float sourceRadius, bool castShadows = true)
    {
        Vector3 normalizedDirection = NormalizeOrFallback(direction, LightMath.SpotLocalDirection);
        var light = new LightNode
        {
            Type = "spot",
            Position = new[] { position.X, position.Y, position.Z },
            Direction = new[] { normalizedDirection.X, normalizedDirection.Y, normalizedDirection.Z },
            Color = new[] { color.X, color.Y, color.Z },
            Intensity = intensity,
            Range = range,
            InnerCone = innerCone,
            OuterCone = outerCone,
            SourceRadius = sourceRadius,
            CastShadows = castShadows
        };
        _currentScene?.Lights.Add(light);
        return CreateLightEntity(light);
    }

    private ulong CreateLightEntity(LightNode light)
    {
        if (_world == null) return 0;

        ulong ent = _world.CreateEntity();
        Vector3 position = ReadVector3(light.Position, Vector3.Zero);
        Vector3 direction = NormalizeOrFallback(ReadVector3(light.Direction, LightMath.SpotLocalDirection), LightMath.SpotLocalDirection);
        Quaternion rotation =
            light.Type is "spot" or "directional"
            ? LightMath.GetSpotRotation(direction)
            : Quaternion.Identity;

        _world.Set(ent, new Engine.Scene.Components.Transform
        {
            Position = position,
            Rotation = rotation,
            Scale = Vector3.One
        });

        switch (light.Type)
        {
            case "point":
                _world.Set(ent, new PointLightComponent
                {
                    Color = ReadColor(light.Color, Vector3.One),
                    Intensity = light.Intensity,
                    Range = light.Range,
                    SourceRadius = light.SourceRadius,
                    CastShadows = light.CastShadows
                });
                break;
            case "spot":
                _world.Set(ent, new SpotLightComponent
                {
                    Color = ReadColor(light.Color, Vector3.One),
                    Intensity = light.Intensity,
                    Range = light.Range,
                    Direction = direction,
                    InnerCone = light.InnerCone,
                    OuterCone = light.OuterCone,
                    SourceRadius = light.SourceRadius,
                    CastShadows = light.CastShadows
                });
                break;
            default:
                _world.Set(ent, new DirectionalLightComponent
                {
                    Color = ReadColor(light.Color, Vector3.One),
                    Intensity = light.Intensity,
                    Direction = direction,
                    AngularRadius = light.SunRadius,
                    CastShadows = light.CastShadows
                });
                break;
        }

        return ent;
    }

    private static Vector3 ReadVector3(float[] values, Vector3 fallback)
    {
        return new Vector3(
            values.Length > 0 ? values[0] : fallback.X,
            values.Length > 1 ? values[1] : fallback.Y,
            values.Length > 2 ? values[2] : fallback.Z);
    }

    private static Vector3 ReadColor(float[] values, Vector3 fallback)
    {
        return ReadVector3(values, fallback);
    }

    private static Vector3 NormalizeOrFallback(Vector3 value, Vector3 fallback)
    {
        return LightMath.NormalizeOrFallback(value, fallback);
    }

    private void RebuildRenderPlan(SceneGraph scene, string contentRoot)
    {
        DisposeRenderPlans();
        ActivateOrCompileRenderPlan(scene, contentRoot);
    }

    public void ReloadPluginShaders(string pluginId)
    {
        if (_currentScene == null)
            return;

        bool pathTracing =
            pluginId == "core.renderer.path-tracing";
        CachedRenderPlan? stale = pathTracing
            ? _pathTracingPlan
            : _rasterPlan;
        bool wasActive =
            stale != null &&
            _plan == stale.Plan;
        stale?.Dispose();
        if (pathTracing)
            _pathTracingPlan = null;
        else
            _rasterPlan = null;

        if (wasActive)
        {
            _plan = null;
            _rasterSceneCache = null;
            _directionalShadowState = null;
            _directionalShadowPass = null;
            _punctualShadowState = null;
            _punctualShadowPass = null;
            ActivateOrCompileRenderPlan(
                _currentScene,
                _contentRoot);
        }
    }

    private void ActivateOrCompileRenderPlan(
        SceneGraph scene,
        string contentRoot)
    {
        CachedRenderPlan? state = _usePathTracer
            ? _pathTracingPlan
            : _rasterPlan;
        if (state == null)
        {
            state = CompileRenderPlan(
                scene,
                contentRoot,
                _usePathTracer);
            if (_usePathTracer)
                _pathTracingPlan = state;
            else
                _rasterPlan = state;
        }

        ActivateRenderPlan(state);
    }

    private CachedRenderPlan CompileRenderPlan(
        SceneGraph scene,
        string contentRoot,
        bool usePathTracer)
    {
        var passes = new List<RenderPass>();
        IRendererPlanPlugin? plugin =
            usePathTracer
                ? _pathTracingPlugin
                : _clusteredPlugin;
        if (plugin == null)
        {
            throw new InvalidOperationException(
                usePathTracer
                    ? "Path-tracing renderer plugin is not loaded."
                    : "Required clustered renderer plugin is not loaded.");
        }

        var pluginContext =
            new RendererPluginContext
            {
                Device = _device,
                World = _world,
                Scene = scene,
                ContentRoot = contentRoot,
                BindlessHeap = _sharedBindlessHeap,
                Renderer = this,
                GpuWorkScheduler =
                    _gpuWorkScheduler,
                RenderShadows = _renderShadows,
                RenderSky = _renderSky
            };
        RendererPluginPlan pluginPlan =
            plugin.BuildPlan(pluginContext);
        passes.AddRange(pluginPlan.Passes);

        if (!usePathTracer &&
            pluginPlan.RasterSceneCache == null)
        {
            throw new InvalidOperationException(
                "Clustered renderer plugin did not create a raster scene cache.");
        }

        passes.Add(new OutlineMaskPass(_device, _world, scene, contentRoot, this));
        passes.Add(new OutlineCompositePass(_device, contentRoot, this));

        if (_renderGrid)
        {
            passes.Add(new GridPass(_device, _world, contentRoot, this, clearScreen: scene.Passes.Count == 0));
        }

        if (_imguiRenderer != null)
            passes.Add(new ImGuiPass(_imguiRenderer));

        Info($"[Renderer] Compiling render graph with {passes.Count} pass(es)...", "Renderer");
        var newPlan = new RenderGraphCompiler().Compile(passes);

        Info("[Renderer] Render graph compiled successfully", "Renderer");
        return new CachedRenderPlan
        {
            Plan = newPlan,
            RasterSceneCache =
                pluginPlan.RasterSceneCache,
            DirectionalShadowState =
                pluginPlan.DirectionalShadowState,
            DirectionalShadowPass =
                pluginPlan.DirectionalShadowPass,
            PunctualShadowState =
                pluginPlan.PunctualShadowState,
            PunctualShadowPass =
                pluginPlan.PunctualShadowPass
        };
    }

    private void ActivateRenderPlan(CachedRenderPlan state)
    {
        _plan = state.Plan;
        _rasterSceneCache = state.RasterSceneCache;
        _directionalShadowState = state.DirectionalShadowState;
        _directionalShadowPass = state.DirectionalShadowPass;
        _punctualShadowState = state.PunctualShadowState;
        _punctualShadowPass = state.PunctualShadowPass;
        _renderPlanVersion++;
    }

    private void DisposeRenderPlans()
    {
        RenderPlan? activePlan = _plan;
        bool activePlanIsCached =
            activePlan != null &&
            (activePlan == _rasterPlan?.Plan ||
             activePlan == _pathTracingPlan?.Plan);
        _rasterPlan?.Dispose();
        _pathTracingPlan?.Dispose();
        if (!activePlanIsCached)
            activePlan?.Passes.DisposeAll();

        _rasterPlan = null;
        _pathTracingPlan = null;
        _plan = null;
        _rasterSceneCache = null;
        _directionalShadowState = null;
        _directionalShadowPass = null;
        _punctualShadowState = null;
        _punctualShadowPass = null;
    }

    public void RenderFrame(RhiTexture backBuffer, uint width, uint height, RhiFence? syncFence = null, ulong waitValue = 0, ulong signalValue = 0)
    {
        if (_plan is null) return;

        if (_depthTexture == null || _depthWidth != width || _depthHeight != height)
        {
            _depthTexture?.Dispose();
            _outlineMaskTexture?.Dispose();

            _depthWidth = width > 0 ? width : 1;
            _depthHeight = height > 0 ? height : 1;

            var desc = new Engine.CBindings.RhiNative.TextureDesc
            {
                Abi = 1,
                Width = _depthWidth,
                Height = _depthHeight,
                MipLevels = 1,
                Format = Engine.CBindings.RhiNative.TextureFormat.Depth32Float,
                UsageFlags = Engine.CBindings.RhiNative.TextureRenderTarget
            };
            _depthTexture = RhiTexture.CreateDepth(_device, _depthWidth, _depthHeight);

            _outlineMaskTexture = RhiTexture.CreateRenderTarget(_device, _depthWidth, _depthHeight, Engine.CBindings.RhiNative.TextureFormat.Bgra8Unorm);
        }

        _graphExecutor.SetViewportSize(width, height);
        _graphExecutor.BindSwapchain(backBuffer, BackBufferHandle, ResourceState.RenderTarget);
        if (_depthTexture != null)
            _graphExecutor.BindSwapchain(_depthTexture, DepthBufferHandle, ResourceState.DepthStencil);
        if (_outlineMaskTexture != null)
            _graphExecutor.BindSwapchain(_outlineMaskTexture, OutlineMaskHandle, ResourceState.RenderTarget);
        if (_directionalShadowState != null)
        {
            int pageCount = Math.Min(
                _directionalShadowState.Atlas.Pages.Count,
                24);
            for (int pageIndex = 0;
                 pageIndex < pageCount;
                 ++pageIndex)
            {
                _graphExecutor.BindSwapchain(
                    _directionalShadowState.Atlas.Pages[pageIndex],
                    GetShadowPageHandle(pageIndex),
                    ResourceState.ShaderRead);
            }
            for (int pageIndex = pageCount; pageIndex < 24; ++pageIndex)
                _graphExecutor.UnbindTexture(GetShadowPageHandle(pageIndex));
        }
        else
        {
            for (int pageIndex = 0; pageIndex < 24; ++pageIndex)
                _graphExecutor.UnbindTexture(GetShadowPageHandle(pageIndex));
        }

        _graphExecutor.Execute(_plan, syncFence, waitValue, syncFence, signalValue);
        ConsumeGpuWorkTimings();
        _renderedFrameCount++;
    }

    private void ConsumeGpuWorkTimings()
    {
        long timingFrame = _graphExecutor.LastGpuTimingFrameNumber;
        if (timingFrame < 0 ||
            timingFrame == _lastConsumedGpuTimingFrame)
        {
            return;
        }

        _lastConsumedGpuTimingFrame = timingFrame;
        if (_graphExecutor.LastGpuFrameMilliseconds is
                double completedFrameMilliseconds)
        {
            _gpuWorkScheduler.RecordFrameGpuTime(
                completedFrameMilliseconds);
        }
        var timings = _graphExecutor.LastPassTimings;
        for (int i = 0; i < timings.Count; ++i)
        {
            if (timings[i].GpuMilliseconds is not double milliseconds)
            {
                continue;
            }
            if (_graphExecutor.LastRawGpuFrameMilliseconds is
                    double frameMilliseconds &&
                milliseconds > frameMilliseconds * 1.05)
            {
                continue;
            }
            if (_directionalShadowPass != null &&
                timings[i].Name.Equals(
                    _directionalShadowPass.Name,
                    StringComparison.Ordinal) &&
                _directionalShadowPass.TryGetRenderedCascadeCount(
                    timingFrame,
                    out int cascadeCount) &&
                cascadeCount > 0)
            {
                _gpuWorkScheduler.RecordCompletedWork(
                    GpuWorkDomain.Shadows,
                    milliseconds,
                    cascadeCount);
            }
            else if (_punctualShadowPass != null &&
                timings[i].Name.Equals(
                    _punctualShadowPass.Name,
                    StringComparison.Ordinal) &&
                _punctualShadowPass.TryGetRenderedUnitCount(
                    timingFrame,
                    out int unitCount) &&
                unitCount > 0)
            {
                _gpuWorkScheduler.RecordCompletedWork(
                    GpuWorkDomain.PunctualShadows,
                    milliseconds,
                    unitCount);
            }
        }
    }

    public RenderGraphDiagnosticsSnapshot? GetRenderGraphDiagnostics()
    {
        if (_plan == null)
            return null;

        var passTimings = _graphExecutor.LastPassTimings;
        var passes = new RenderGraphPassDiagnostics[_plan.Passes.Length];
        var lastWriters = new Dictionary<ResourceHandle, int>();
        double cpuTotal = 0.0;

        for (int i = 0; i < _plan.Passes.Length; ++i)
        {
            double cpuMilliseconds = i < passTimings.Count
                ? passTimings[i].CpuMilliseconds
                : 0.0;
            cpuTotal += cpuMilliseconds;

            var accesses = new RenderGraphAccessDiagnostics[_plan.PassAccesses[i].Count];
            var dependencies = new HashSet<string>();
            for (int accessIndex = 0; accessIndex < accesses.Length; ++accessIndex)
            {
                var access = _plan.PassAccesses[i][accessIndex];
                if (access.Access != ResourceAccess.Write &&
                    lastWriters.TryGetValue(access.Resource, out int writerIndex) &&
                    writerIndex != i)
                {
                    dependencies.Add(_plan.Passes[writerIndex].Name);
                }
                accesses[accessIndex] = new RenderGraphAccessDiagnostics(
                    access.Resource.Id,
                    GetResourceName(access.Resource),
                    access.Access.ToString(),
                    access.State.ToString());
                if (access.Access != ResourceAccess.Read)
                    lastWriters[access.Resource] = i;
            }

            var barriers = new RenderGraphBarrierDiagnostics[_plan.BarriersPerPass[i].Count];
            for (int barrierIndex = 0; barrierIndex < barriers.Length; ++barrierIndex)
            {
                var barrier = _plan.BarriersPerPass[i][barrierIndex];
                barriers[barrierIndex] = new RenderGraphBarrierDiagnostics(
                    barrier.Resource.Id,
                    GetResourceName(barrier.Resource),
                    barrier.StateBefore.ToString(),
                    barrier.StateAfter.ToString());
            }

            passes[i] = new RenderGraphPassDiagnostics(
                _plan.Passes[i].Name,
                _plan.Passes[i].Queue.ToString(),
                cpuMilliseconds,
                i < passTimings.Count ? passTimings[i].GpuMilliseconds : null,
                dependencies.ToArray(),
                accesses,
                barriers);
        }

        var resourceLifetimes = new Dictionary<ResourceHandle, (int First, int Last, int Count)>();
        for (int passIndex = 0; passIndex < _plan.PassAccesses.Count; ++passIndex)
        {
            foreach (var access in _plan.PassAccesses[passIndex])
            {
                if (resourceLifetimes.TryGetValue(access.Resource, out var lifetime))
                {
                    resourceLifetimes[access.Resource] =
                        (lifetime.First, passIndex, lifetime.Count + 1);
                }
                else
                {
                    resourceLifetimes[access.Resource] = (passIndex, passIndex, 1);
                }
            }
        }

        var aliasGroups = new Dictionary<ResourceHandle, string>();
        int aliasGroupIndex = 0;
        foreach (var group in _plan.Aliasing.ResourceOffsets.GroupBy(entry => entry.Value))
        {
            if (group.Count() < 2)
                continue;
            string groupName = $"A{aliasGroupIndex++}";
            foreach (var entry in group)
                aliasGroups[entry.Key] = groupName;
        }

        var resources = new List<RenderGraphResourceDiagnostics>();
        var reportedResources = new HashSet<ResourceHandle>();
        foreach (var passAccesses in _plan.PassAccesses)
        {
            foreach (var access in passAccesses)
            {
                if (_plan.ResourceDecls.ContainsKey(access.Resource) ||
                    !reportedResources.Add(access.Resource))
                {
                    continue;
                }

                ulong importedSize = IsShadowPageHandle(access.Resource)
                    ? ShadowAtlas.BytesPerPage
                    : access.Resource == BackBufferHandle ||
                        access.Resource == DepthBufferHandle ||
                        access.Resource == OutlineMaskHandle
                        ? (ulong)_depthWidth * _depthHeight * 4ul
                        : 0;
                resources.Add(new RenderGraphResourceDiagnostics(
                    access.Resource.Id,
                    GetResourceName(access.Resource),
                    "Imported",
                    importedSize,
                    0,
                    "-",
                    resourceLifetimes[access.Resource].First,
                    resourceLifetimes[access.Resource].Last,
                    resourceLifetimes[access.Resource].Count));
            }
        }

        foreach (var (handle, declaration) in _plan.ResourceDecls)
        {
            _plan.Aliasing.ResourceOffsets.TryGetValue(handle, out ulong aliasOffset);
            resourceLifetimes.TryGetValue(handle, out var lifetime);
            resources.Add(new RenderGraphResourceDiagnostics(
                handle.Id,
                GetResourceName(handle),
                declaration.Kind.ToString(),
                GetResourceSize(declaration),
                aliasOffset,
                aliasGroups.GetValueOrDefault(handle, "-"),
                lifetime.First,
                lifetime.Last,
                lifetime.Count));
        }

        GpuWorkBudgetSnapshot[] workBudgets = _gpuWorkScheduler.GetSnapshots();
        GpuResourceAllocationDiagnostics[] allocations =
            GpuResourceRegistry.Capture();
        return new RenderGraphDiagnosticsSnapshot(
            _renderPlanVersion,
            _renderedFrameCount,
            cpuTotal,
            _graphExecutor.LastGpuFrameMilliseconds,
            _plan.Aliasing.TotalHeapSize,
            allocations.Aggregate(
                0ul,
                (total, allocation) =>
                    total + allocation.SizeBytes),
            allocations,
            workBudgets.Select(budget => new RenderGraphBudgetDiagnostics(
                budget.Name,
                budget.BudgetMilliseconds,
                budget.EstimatedUnitMilliseconds,
                budget.MaximumUnits,
                budget.AdmittedUnits,
                budget.DeferredUnits,
                budget.TotalAdmittedUnits,
                budget.TotalDeferredUnits)).ToArray(),
            passes,
            resources.ToArray(),
            _punctualShadowState?.GetDiagnostics());
    }

    public bool RenderShadowAtlasTilePreview(
        ulong entityId,
        int faceIndex,
        bool dynamicTile,
        RhiTexture target,
        uint width,
        uint height,
        RhiFence? syncFence,
        ulong waitValue,
        ulong signalValue)
    {
        if (_punctualShadowState == null ||
            !_punctualShadowState.TryGetTile(
                entityId,
                faceIndex,
                dynamicTile,
                out ShadowAtlasAllocation tile))
        {
            return false;
        }

        _shadowAtlasPreviewRenderer ??=
            new ShadowAtlasPreviewRenderer(
                _device,
                _contentRoot);
        _shadowAtlasPreviewRenderer.Render(
            tile,
            target,
            width,
            height,
            syncFence,
            waitValue,
            signalValue);
        return true;
    }

    private static string GetResourceName(ResourceHandle handle)
    {
        if (handle == BackBufferHandle) return "Back Buffer";
        if (handle == DepthBufferHandle) return "Scene Depth";
        if (handle == OutlineMaskHandle) return "Outline Mask";
        if (IsShadowPageHandle(handle))
        {
            uint pageIndex = GetShadowPageIndex(handle);
            return pageIndex < DirectionalShadowState.CascadeCount
                ? $"Directional Shadow Cascade {pageIndex}"
                : $"Punctual Shadow Page {pageIndex}";
        }
        return $"Resource 0x{handle.Id:X8}";
    }

    private static bool IsShadowPageHandle(ResourceHandle handle)
        => handle.Id >= DirectionalShadowMapHandleBase &&
            handle.Id <
                DirectionalShadowMapHandleBase +
                24u;

    private static uint GetShadowPageIndex(ResourceHandle handle)
        => handle.Id - DirectionalShadowMapHandleBase;

    private static ulong GetResourceSize(ResourceDecl declaration)
    {
        if (declaration.Kind == ResourceKind.Buffer)
            return declaration.Buffer?.Size ?? 0;
        if (declaration.Texture == null)
            return 0;

        ulong bytesPerPixel = declaration.Texture.Format == Engine.CBindings.RhiNative.TextureFormat.Rgba16Float
            ? 8ul
            : 4ul;
        return (ulong)declaration.Texture.Width *
            declaration.Texture.Height *
            bytesPerPixel;
    }

    public ulong Pick(uint x, uint y, uint w, uint h)
    {
        if (_currentScene == null) return 0;
        using var pass = new IdPickingPass(_device, _world, _contentRoot, this);
        pass.PickRequested = true;
        pass.PickX = x;
        pass.PickY = y;

        using var executor = new RenderGraphExecutor(_device);
        executor.SetViewportSize(w, h);
        RenderPlan pickPlan = new RenderGraphCompiler().Compile(new RenderPass[] { pass });
        executor.Execute(pickPlan);

        return pass.PickedId;
    }

    public (ulong EntityId, uint PartIndex) PickSubmesh(uint x, uint y, uint w, uint h)
    {
        if (_currentScene == null) return (0, 0);
        using var pass = new IdPickingPass(_device, _world, _contentRoot, this);
        pass.PickRequested = true;
        pass.PickX = x;
        pass.PickY = y;

        using var executor = new RenderGraphExecutor(_device);
        executor.SetViewportSize(w, h);
        RenderPlan pickPlan = new RenderGraphCompiler().Compile(new RenderPass[] { pass });
        executor.Execute(pickPlan);

        return (pass.PickedId, pass.PickedPartIndex);
    }


    public void Dispose()
    {
        DisposeRenderPlans();
        _loader = null;
        _depthTexture?.Dispose();
        _depthTexture = null;
        _outlineMaskTexture?.Dispose();
        _outlineMaskTexture = null;
        _graphExecutor.Dispose();
        _shadowAtlasPreviewRenderer?.Dispose();
        _shadowAtlasPreviewRenderer = null;
        _sharedBindlessHeap?.Dispose();
        _sharedBindlessHeap = null!;
    }
}

internal static class RenderPassEnumerableExtensions
{
    public static void DisposeAll(this IEnumerable<RenderPass> passes)
    {
        foreach (var p in passes)
            if (p is IDisposable d) d.Dispose();
    }
}
