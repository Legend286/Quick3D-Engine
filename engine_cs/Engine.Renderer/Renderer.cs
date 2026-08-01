// SPDX-License-Identifier: MIT
// Renderer orchestration: takes a Scene, builds the pass list, compiles the
// render graph, drives the executor each frame.
//
// World ownership: the renderer does NOT own the EcsWorld; the caller hands
// it in at construction. This keeps the renderer free of GC pressure and
// lets the editor share a single world across multiple viewports later.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.IO;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.RenderGraph.Shaders;
using Engine.Scene;
using Engine.Scene.Components;
using Engine.Assets;
using Engine.Plugins;
using static Engine.CBindings.Log;

namespace Engine.Renderer;

public sealed class Renderer : IDisposable, IActiveCameraDataProvider
{
    private readonly int _renderThreadId;
    private readonly bool _participatesInGlobalExtensions;
    private readonly bool _enableVisibilityBuffer;
    private readonly ConcurrentQueue<Action<Renderer>>
        _renderThreadActions = new();

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

    private static Renderer? s_active;

    /// <summary>Gets the renderer registered as the host viewport's active
    /// camera provider. Temporary thumbnail renderers must opt out of this
    /// registration so plugin camera queries cannot be redirected to a
    /// short-lived renderer with a different world.</summary>
    public static Renderer? ActiveInstance => s_active;

    private readonly RhiDevice _device;
    private readonly RhiSwapchain _swap;
    private readonly IEntityStore _world;
    private SceneLoader? _loader;
    private string _contentRoot = "Content";
    private readonly ImGuiRenderer? _imguiRenderer;

    private RenderPlan? _plan;
    private SceneGraph? _currentScene;
    private readonly RenderGraphExecutor _graphExecutor;
    private DDGIAtlasResourceHandles? _boundDDGIHandles;
    private readonly GpuWorkScheduler _gpuWorkScheduler = new();
    private long _renderedFrameCount;
    private long _renderPlanVersion;
    private CachedRenderPlan? _rasterPlan;
    private CachedRenderPlan? _pathTracingPlan;

    /// <summary>Back-buffer sentinel. Lives on <see cref="Engine.RenderGraph.RenderGraphResources"/>
    /// now so plugins can reference the same handle without depending
    /// on this assembly. The constants are kept here as delegating
    /// shims for the host internals that still touch them.</summary>
    public static readonly ResourceHandle BackBufferHandle =
        Engine.RenderGraph.RenderGraphResources.BackBufferHandle;
    public static readonly ResourceHandle DepthBufferHandle =
        Engine.RenderGraph.RenderGraphResources.DepthBufferHandle;
    public static readonly ResourceHandle OutlineMaskHandle =
        Engine.RenderGraph.RenderGraphResources.OutlineMaskHandle;
    public static readonly ResourceHandle VisibilityIdentifiersHandle =
        Engine.RenderGraph.RenderGraphResources.VisibilityIdentifiersHandle;
    public static readonly ResourceHandle VisibilityBarycentricsHandle =
        Engine.RenderGraph.RenderGraphResources.VisibilityBarycentricsHandle;
    public static readonly ResourceHandle VisibilityReconstructionHandle =
        Engine.RenderGraph.RenderGraphResources.VisibilityReconstructionHandle;
    public static readonly ResourceHandle VisibilityReferenceHandle =
        Engine.RenderGraph.RenderGraphResources.VisibilityReferenceHandle;
    private const uint DirectionalShadowMapHandleBase = 0x80000003;

    /// <summary>The well-known identifier of the canonical Forward+
    /// raster renderer plugin. Used by the editor-bridge subscription
    /// handler to bust the raster render plan when the active shader
    /// feature set changes.</summary>
    public const string ClusteredPluginId = "core.renderer.clustered";

    /// <summary>The well-known identifier of the canonical path-tracing
    /// renderer plugin. Used by <see cref="ReloadPluginShaders"/> to
    /// decide which cached plan to bust when the engine asks for a
    /// shader rebuild.</summary>
    public const string PathTracingPluginId = "core.renderer.path-tracing";

    public static ResourceHandle GetDirectionalShadowMapHandle(int cascadeIndex)
        => Engine.RenderGraph.RenderGraphResources.GetShadowPageHandle(cascadeIndex);

    public static ResourceHandle GetShadowPageHandle(int pageIndex)
        => Engine.RenderGraph.RenderGraphResources.GetShadowPageHandle(pageIndex);

    private string _lastSceneName = "";
    private bool _usePathTracer;
    private bool _renderSky = true;
    private bool _renderGrid = true;
    private bool _renderShadows = true;
    private RhiBindlessHeap _sharedBindlessHeap;
    private ShaderCompileCache _compileCache = new();

    /// <summary>Process-wide shader compile cache. Plugins and
    /// passes thread shader compilations through this cache via
    /// <see cref="ShaderCompileCache.GetOrCompileHash"/> so toggling
    /// plugins that don't actually change a shader's source bytes
    /// return the existing compiled <see cref="Engine.RHI.RhiShader"/>
    /// handle instead of forcing a Slang recompile + Metal pipeline
    /// state recreation. See
    /// <c>docs/renderer/shader-cache.md</c> for the public API and
    /// perf characteristics.</summary>
    public ShaderCompileCache ShaderCompileCache => _compileCache;
    private IReadOnlyList<string>? _activeShaderCliArgs;
    private IReadOnlyList<string>? _activeShaderIncludeDirs;

    public IReadOnlyList<string>? ActiveShaderIncludeDirs => _activeShaderIncludeDirs;

    public string LoadShaderSource(string relPath, string contentRoot)
    {
        string full = Path.Combine(contentRoot, relPath);
        if (File.Exists(full)) return File.ReadAllText(full);

        if (_activeShaderIncludeDirs != null)
        {
            string fileName = Path.GetFileName(relPath);
            foreach (var dir in _activeShaderIncludeDirs)
            {
                string fallback = Path.Combine(dir, fileName);
                if (File.Exists(fallback)) return File.ReadAllText(fallback);

                string parentFallback = Path.Combine(Path.GetDirectoryName(dir) ?? dir, relPath);
                if (File.Exists(parentFallback)) return File.ReadAllText(parentFallback);
            }
        }
        throw new FileNotFoundException(full);
    }

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
    private readonly List<IRendererPlanPlugin> _extensionPlugins = new();

    private RhiTexture? _depthTexture;
    private RhiTexture? _outlineMaskTexture;
    private RhiTexture? _visibilityIdentifiersTexture;
    private RhiTexture? _visibilityBarycentricsTexture;
    private RhiTexture? _visibilityReconstructionTexture;
    private RhiTexture? _visibilityReferenceTexture;
    private uint _depthWidth, _depthHeight;

    private ulong _selectedEntity;
    public ulong SelectedEntity
    {
        get => _selectedEntity;
        set { _selectedEntity = value; }
    }

    public ulong ActiveCameraEntity { get; set; }
    public float OrthographicSize { get; set; } = 20.0f;

    /// <summary>Reads the active scene-camera entity's
    /// <see cref="Engine.Scene.Components.Camera"/> + Transform
    /// components out of the world and produces the matching
    /// <see cref="CameraData"/> for the given viewport
    /// <paramref name="width"/> × <paramref name="height"/>. Returns
    /// false when no active camera entity is set or the entity does
    /// not carry a <see cref="Engine.Scene.Components.Camera"/>
    /// component; callers should fall back to identity viewProj in
    /// that case.</summary>
    public bool TryGetActiveCameraData(
        uint width, uint height,
        out Engine.Scene.Components.Camera camera,
        out Engine.Scene.Components.Transform transform,
        out CameraData cameraData)
    {
        camera = default;
        transform = default;
        cameraData = default;
        if (_world is null || ActiveCameraEntity == 0)
            return false;
        if (!_world.TryGet(
                ActiveCameraEntity,
                out camera))
            return false;
        transform = _world.TryGet(
                ActiveCameraEntity,
                out Engine.Scene.Components.Transform t)
            ? t
            : Engine.Scene.Components.Transform.Default;
        if (height == 0)
            return false;
        float aspect = (float)width / height;
        cameraData = BuildCameraData(
            camera, transform, aspect, Vector3.UnitZ);
        return true;
    }
    bool IActiveCameraDataProvider.TryGetViewportCameraData(
        uint width,
        uint height,
        out Vector3 cameraPosition,
        out Matrix4x4 viewProjection,
        out Matrix4x4 inverseViewProjection)
    {
        cameraPosition = Vector3.Zero;
        viewProjection = Matrix4x4.Identity;
        inverseViewProjection = Matrix4x4.Identity;
        if (!TryGetActiveCameraData(
                width,
                height,
                out _,
                out Transform transform,
                out CameraData cameraData))
        {
            return false;
        }

        cameraPosition = transform.Position;
        viewProjection = cameraData.ViewProj;
        inverseViewProjection = cameraData.InvViewProj;
        return true;
    }

    internal float ProjectionBlend => _projectionBlend;

    /// <summary>Per-frame upstream-derived Slang <c>-D</c> argv tokens,
    /// refreshed whenever <see cref="ReloadPluginShaders"/> is invoked.
    /// Plugins read this via <c>RendererPluginContext.ShaderCliArgs</c> and
    /// forward it to <c>RhiShader.FromSource(... cliArgs)</c> so host shaders
    /// can gate plugin-shader override paths.</summary>
    public IReadOnlyList<string>? ActiveShaderCliArgs =>
        _activeShaderCliArgs;
    internal IRendererPlanPlugin? ClusteredPlugin => _clusteredPlugin;

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
            AssertRenderThread();
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
        IRendererPlanPlugin? pathTracingPlugin = null,
        bool registerAsActive = true,
        bool enableVisibilityBuffer = true)
    {
        _renderThreadId = Environment.CurrentManagedThreadId;
        _participatesInGlobalExtensions = registerAsActive;
        _enableVisibilityBuffer = enableVisibilityBuffer;
        if (registerAsActive)
            s_active = this;
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
        EditorShaderBridge.ActiveShaderContextChanged += OnActiveShaderContextChanged;
        _activeShaderCliArgs = EditorShaderBridge.LastCliArgs;
        _activeShaderIncludeDirs = EditorShaderBridge.LastIncludeDirs;
    }

    private void OnActiveShaderContextChanged(
        IReadOnlyList<string>? cliArgs,
        IReadOnlyList<string>? includeDirs)
    {
        _activeShaderCliArgs = cliArgs;
        _activeShaderIncludeDirs = includeDirs;
        ReloadPluginShaders(ClusteredPluginId);
    }

    public IEntityStore World => _world;

    /// <summary>Most-recently loaded scene graph. <see cref="Engine.Game.GameLoop"/>
    /// consults <c>CurrentScene.ProceduralDemo</c> after delegating to
    /// <see cref="LoadScene"/> so the procedural-demo builder can
    /// expand its <c>ProceduralDemoDefinition</c> into world entities
    /// without <c>Engine.Game</c> taking a hard dependency on
    /// <c>Engine.Renderer</c>'s private state.</summary>
    public SceneGraph? CurrentScene => _currentScene;

    /// <summary>Gets whether renderer-owned background work needs another frame.</summary>
    public bool HasPendingRenderWork =>
        _participatesInGlobalExtensions &&
        (DDGIAtlasProviderRegistry.Active?.HasPendingWork ?? false);

    /// <summary>True once a scene has been loaded via <see cref="LoadScene"/>
    /// and the render plan has been compiled. Used by plugin-activation
    /// background tasks to avoid calling <see cref="AddExtensionPlugin"/>
    /// before <see cref="InvalidateRenderPlan"/> can trigger a recompile.</summary>
    public bool HasActiveScene => _currentScene != null;

    public void SetPathTracingPlugin(
        IRendererPlanPlugin? plugin)
    {
        if (!IsRenderThread)
        {
            EnqueueRenderThreadAction(
                renderer => renderer.SetPathTracingPlugin(plugin));
            return;
        }
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

    /// <summary>Adds a renderer-extension plugin whose
    /// <see cref="IRendererPlanPlugin.BuildPlan"/> contribution is
    /// composed into the active render graph each frame alongside
    /// the primary clustered or path-tracing plan. Toggling off
    /// restores the canonical plan; extension plugins are expected
    /// to drop their slot registrations (atlas slots, bindless
    /// resources, etc.) inside <c>Shutdown</c> so the host's
    /// shader-side <c>#ifdef NAME</c> limbs gracefully fall back
    /// to no-extension sampling.</summary>
    public void AddExtensionPlugin(
        IRendererPlanPlugin extension)
    {
        if (!IsRenderThread)
        {
            EnqueueRenderThreadAction(
                renderer => renderer.AddExtensionPlugin(extension));
            return;
        }
        if (extension == null) return;
        if (_extensionPlugins.Contains(extension)) return;
        _extensionPlugins.Add(extension);
        InvalidateRenderPlan();
    }

    /// <summary>Removes a renderer-extension plugin and rebuilds
    /// the canonical render graph; companion to
    /// <see cref="AddExtensionPlugin"/>.</summary>
    public void RemoveExtensionPlugin(
        IRendererPlanPlugin extension)
    {
        if (!IsRenderThread)
        {
            EnqueueRenderThreadAction(
                renderer => renderer.RemoveExtensionPlugin(extension));
            return;
        }
        if (extension == null) return;
        if (!_extensionPlugins.Remove(extension)) return;
        InvalidateRenderPlan();
    }

    public void InvalidateRenderPlan()
    {
        if (!IsRenderThread)
        {
            EnqueueRenderThreadAction(
                renderer => renderer.InvalidateRenderPlan());
            return;
        }
        _rasterPlan?.Dispose();
        _pathTracingPlan?.Dispose();
        _rasterPlan = null;
        _pathTracingPlan = null;
        _plan = null;
        _rasterSceneCache = null;
        _directionalShadowState = null;
        _directionalShadowPass = null;
        _punctualShadowState = null;
        _punctualShadowPass = null;
        if (_currentScene != null)
            ActivateOrCompileRenderPlan(
                _currentScene,
                _contentRoot);
        _renderPlanVersion++;
    }

    /// <summary>Gets whether the caller owns this renderer's RHI objects.</summary>
    public bool IsRenderThread =>
        Environment.CurrentManagedThreadId == _renderThreadId;

    /// <summary>
    /// Executes work on the renderer owner or queues it for the next frame.
    /// </summary>
    public void EnqueueRenderThreadAction(Action<Renderer> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (IsRenderThread)
        {
            action(this);
            return;
        }
        _renderThreadActions.Enqueue(action);
    }

    private void DrainRenderThreadActions()
    {
        while (_renderThreadActions.TryDequeue(
                   out Action<Renderer>? action))
        {
            action(this);
        }
    }

    private void AssertRenderThread()
    {
        if (!IsRenderThread)
        {
            throw new InvalidOperationException(
                "Renderer graphics work must execute on its owner thread.");
        }
    }

    public void SetClusteredPlugin(
        IRendererPlanPlugin? plugin)
    {
        if (!IsRenderThread)
        {
            EnqueueRenderThreadAction(
                renderer => renderer.SetClusteredPlugin(plugin));
            return;
        }
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
        AssertRenderThread();
        _contentRoot = contentRoot;
        _lastSceneName = sceneName;
        _loader = new SceneLoader(contentRoot);
        SceneGraph scene = _loader.Load(sceneName);

        // Wait for all in-flight GPU commands to complete before destroying the old scene's resources
        using (var cmd = new Engine.RHI.CommandRecorder(_device, Engine.CBindings.RhiNative.QueueType.Graphics))
        {
            cmd.SubmitAndWait();
        }

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
        AssertRenderThread();
        var sg = new SceneGraph();
        sg.Passes.Add(new ScenePass { ClearColor = new float[] { 0.15f, 0.15f, 0.15f, 1.0f } });
        sg.Lights.Add(new LightNode { Type = "directional", Color = new float[] { 1.0f, 0.94f, 0.9f }, Intensity = 3.6f, Position = new float[] { 2, 2, -2 }, Direction = new float[] { -0.8f, 1.0f, 0.6f }, SunRadius = 0.01f });
        sg.Lights.Add(new LightNode { Type = "directional", Color = new float[] { 0.72f, 0.82f, 1.0f }, Intensity = 1.2f, Position = new float[] { -2, 1, -2 }, Direction = new float[] { 0.65f, 0.55f, 0.75f }, SunRadius = 0.01f });
        sg.Lights.Add(new LightNode { Type = "directional", Color = new float[] { 1.0f, 1.0f, 1.0f }, Intensity = 0.8f, Position = new float[] { 0, 2, 3 }, Direction = new float[] { 0.15f, 0.9f, -0.4f }, SunRadius = 0.01f });

        _currentScene = sg;
        _usePathTracer = false;
        _renderSky = false;
        _renderGrid = false;
        _renderShadows = false;
        RebuildRenderPlan(sg, contentRoot);
    }

    public void BuildTextureThumbnailPlan(string contentRoot, RhiTexture sourceTexture)
    {
        AssertRenderThread();
        _currentScene = null;
        _renderSky = false;
        _renderGrid = false;
        _renderShadows = false;

        var passes = new List<RenderPass>
        {
            new TextureThumbnailPass(_device, sourceTexture, contentRoot, this)
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
        if (!IsRenderThread)
        {
            EnqueueRenderThreadAction(
                renderer => renderer.ReloadPluginShaders(pluginId));
            return;
        }
        if (_currentScene == null)
            return;

        bool pathTracing =
            pluginId == PathTracingPluginId;
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
        _compileCache.BumpGeneration();
        _compileCache.EvictOlderThan(2);
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
                ActiveCameraProvider = this,
                Renderer = this,
                GpuWorkScheduler =
                    _gpuWorkScheduler,
                RenderShadows = _renderShadows,
                RenderSky = _renderSky,
                EnableGlobalExtensions =
                    _participatesInGlobalExtensions,
                EnableVisibilityBuffer = _enableVisibilityBuffer,
                ShaderCliArgs = _activeShaderCliArgs,
                ShaderIncludeDirs = _activeShaderIncludeDirs,
                SharedShaderCache = _compileCache
            };
        RendererPluginPlan pluginPlan =
            plugin.BuildPlan(pluginContext);
        pluginContext.SceneGpuDataProvider =
            pluginPlan.RasterSceneCache as ISceneGpuDataProvider;

        var extPasses = new List<RenderPass>();
        var extPostPasses = new List<RenderPass>();
        foreach (var ext in _extensionPlugins)
        {
            RendererPluginPlan extPlan =
                ext.BuildPlan(pluginContext);
            extPasses.AddRange(extPlan.Passes);
            extPostPasses.AddRange(extPlan.PostPasses);
        }
        passes.AddRange(extPasses);
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

        if (!usePathTracer &&
            _enableVisibilityBuffer &&
            scene.Passes.Count > 0)
        {
            passes.Add(new VisibilityBufferDebugPass(
                _device,
                contentRoot,
                this));
        }

        passes.AddRange(extPostPasses);

        if (_imguiRenderer != null)
            passes.Add(new ImGuiPass(_imguiRenderer));

        Info($"[Renderer] Compiling render graph with {passes.Count} pass(es)...", "Renderer");
        var newPlan = new RenderGraphCompiler().Compile(passes);

        Info("[Renderer] Render graph compiled successfully", "Renderer");
        return new CachedRenderPlan
        {
            Plan = newPlan,
            RasterSceneCache =
                (Engine.Renderer.RasterSceneGpuCache?)pluginPlan.RasterSceneCache,
            DirectionalShadowState =
                (Engine.Renderer.DirectionalShadowState?)pluginPlan.DirectionalShadowState,
            DirectionalShadowPass =
                (Engine.Renderer.DirectionalShadowPass?)pluginPlan.DirectionalShadowPass,
            PunctualShadowState =
                (Engine.Renderer.PunctualShadowState?)pluginPlan.PunctualShadowState,
            PunctualShadowPass =
                (Engine.Renderer.PunctualShadowPass?)pluginPlan.PunctualShadowPass
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

    private void EnsureVisibilityBufferResources()
    {
        bool required = false;
        if (_plan != null)
        {
            foreach (IReadOnlyList<AccessDecl> accesses in _plan.PassAccesses)
            {
                foreach (AccessDecl access in accesses)
                {
                    if (access.Resource == VisibilityIdentifiersHandle ||
                        access.Resource == VisibilityBarycentricsHandle ||
                        access.Resource == VisibilityReconstructionHandle ||
                        access.Resource == VisibilityReferenceHandle)
                    {
                        required = true;
                        break;
                    }
                }
                if (required)
                    break;
            }
        }

        if (!required)
        {
            _visibilityIdentifiersTexture?.Dispose();
            _visibilityIdentifiersTexture = null;
            _visibilityBarycentricsTexture?.Dispose();
            _visibilityBarycentricsTexture = null;
            _visibilityReconstructionTexture?.Dispose();
            _visibilityReconstructionTexture = null;
            _visibilityReferenceTexture?.Dispose();
            _visibilityReferenceTexture = null;
            _graphExecutor.UnbindTexture(VisibilityIdentifiersHandle);
            _graphExecutor.UnbindTexture(VisibilityBarycentricsHandle);
            _graphExecutor.UnbindTexture(VisibilityReconstructionHandle);
            _graphExecutor.UnbindTexture(VisibilityReferenceHandle);
            return;
        }

        if (_visibilityIdentifiersTexture == null)
        {
            _visibilityIdentifiersTexture = RhiTexture.CreateRenderTarget(
                _device,
                _depthWidth,
                _depthHeight,
                Engine.CBindings.RhiNative.TextureFormat.Rg32Uint);
            _visibilityIdentifiersTexture.SetDebugName(
                "Visibility Identifiers",
                "Visibility Buffer");
        }
        if (_visibilityBarycentricsTexture == null)
        {
            _visibilityBarycentricsTexture = RhiTexture.CreateRenderTarget(
                _device,
                _depthWidth,
                _depthHeight,
                Engine.CBindings.RhiNative.TextureFormat.Rg16Unorm);
            _visibilityBarycentricsTexture.SetDebugName(
                "Visibility Barycentrics",
                "Visibility Buffer");
        }
        bool comparisonRequired =
            _debugView == ViewportDebugView.VisibilityBuffer ||
            VisibilityReconstructionPass.IsReconstructionView(_debugView) ||
            VisibilityShadingPass.IsShadingView(_debugView);
        if (!comparisonRequired)
        {
            _visibilityReconstructionTexture?.Dispose();
            _visibilityReconstructionTexture = null;
            _visibilityReferenceTexture?.Dispose();
            _visibilityReferenceTexture = null;
            _graphExecutor.UnbindTexture(VisibilityReconstructionHandle);
            _graphExecutor.UnbindTexture(VisibilityReferenceHandle);
            return;
        }
        if (_visibilityReconstructionTexture == null)
        {
            _visibilityReconstructionTexture = RhiTexture.CreateStorage(
                _device,
                _depthWidth,
                _depthHeight,
                Engine.CBindings.RhiNative.TextureFormat.Rgba16Float);
            _visibilityReconstructionTexture.SetDebugName(
                "Visibility Reconstruction",
                "Visibility Buffer");
        }
        if (_visibilityReferenceTexture == null)
        {
            _visibilityReferenceTexture = RhiTexture.CreateRenderTarget(
                _device,
                _depthWidth,
                _depthHeight,
                Engine.CBindings.RhiNative.TextureFormat.Rgba16Float);
            _visibilityReferenceTexture.SetDebugName(
                "Visibility Raster Reference",
                "Visibility Buffer");
        }
    }

    public void RenderFrame(RhiTexture backBuffer, uint width, uint height, RhiFence? syncFence = null, ulong waitValue = 0, ulong signalValue = 0)
    {
        AssertRenderThread();
        DrainRenderThreadActions();
        if (_plan is null) return;

        if (_depthTexture == null || _depthWidth != width || _depthHeight != height)
        {
            _depthTexture?.Dispose();
            _outlineMaskTexture?.Dispose();
            _visibilityIdentifiersTexture?.Dispose();
            _visibilityBarycentricsTexture?.Dispose();
            _visibilityReconstructionTexture?.Dispose();
            _visibilityReferenceTexture?.Dispose();
            _visibilityIdentifiersTexture = null;
            _visibilityBarycentricsTexture = null;
            _visibilityReconstructionTexture = null;
            _visibilityReferenceTexture = null;

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

        EnsureVisibilityBufferResources();

        _graphExecutor.SetViewportSize(width, height);
        _graphExecutor.BindSwapchain(backBuffer, BackBufferHandle, ResourceState.RenderTarget);
        if (_depthTexture != null)
            _graphExecutor.BindSwapchain(_depthTexture, DepthBufferHandle, ResourceState.DepthStencil);
        if (_outlineMaskTexture != null)
            _graphExecutor.BindSwapchain(_outlineMaskTexture, OutlineMaskHandle, ResourceState.RenderTarget);
        if (_visibilityIdentifiersTexture != null)
        {
            _graphExecutor.BindSwapchain(
                _visibilityIdentifiersTexture,
                VisibilityIdentifiersHandle,
                ResourceState.RenderTarget);
        }
        if (_visibilityBarycentricsTexture != null)
        {
            _graphExecutor.BindSwapchain(
                _visibilityBarycentricsTexture,
                VisibilityBarycentricsHandle,
                ResourceState.RenderTarget);
        }
        if (_visibilityReconstructionTexture != null)
        {
            _graphExecutor.BindSwapchain(
                _visibilityReconstructionTexture,
                VisibilityReconstructionHandle,
                ResourceState.UnorderedAccess);
        }
        if (_visibilityReferenceTexture != null)
        {
            _graphExecutor.BindSwapchain(
                _visibilityReferenceTexture,
                VisibilityReferenceHandle,
                ResourceState.RenderTarget);
        }
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

        BindDDGIExternalResources();
        _gpuWorkScheduler.BeginFrame(_renderedFrameCount);

        try
        {
            _graphExecutor.Execute(_plan, syncFence, waitValue, syncFence, signalValue);
            ConsumeGpuWorkTimings();
            _renderedFrameCount++;
        }
        catch (System.Exception ex)
        {
            Error(
                "[Renderer] Render frame aborted: " + ex.Message + "\n" + ex.StackTrace,
                "Renderer");
        }
    }

    private void BindDDGIExternalResources()
    {
        IDDGIAtlasProvider? provider =
            _participatesInGlobalExtensions
                ? DDGIAtlasProviderRegistry.Active
                : null;
        if (provider != null &&
            provider.TryGetExternalResources(
                out DDGIAtlasExternalResources resources))
        {
            DDGIAtlasResourceHandles handles = provider.ResourceHandles;
            _graphExecutor.BindExternalBuffer(
                handles.ProbePositions, resources.ProbePositions);
            _graphExecutor.BindExternalBuffer(
                handles.GridToProbeIndex, resources.GridToProbeIndex);
            _graphExecutor.BindExternalBuffer(
                handles.ProbeWorldKeys, resources.ProbeWorldKeys);
            _graphExecutor.BindExternalBuffer(
                handles.WorldProbeHash, resources.WorldProbeHash);
            _graphExecutor.BindExternalBuffer(
                handles.ProbeCounter, resources.ProbeCounter);
            _graphExecutor.BindExternalBuffer(
                handles.ProbeDrawArgs, resources.ProbeDrawArgs);
            _graphExecutor.BindExternalBuffer(
                handles.ProbeStates, resources.ProbeStates);
            _graphExecutor.BindExternalBuffer(
                handles.ProbeUpdateQueue, resources.ProbeUpdateQueue);
            _graphExecutor.BindExternalBuffer(
                handles.VolumeState, resources.VolumeState);
            _graphExecutor.BindExternalTexture(
                handles.Irradiance, resources.Irradiance);
            _graphExecutor.BindExternalTexture(
                handles.Visibility, resources.Visibility);
            _boundDDGIHandles = handles;
            return;
        }

        if (_boundDDGIHandles is not DDGIAtlasResourceHandles oldHandles)
            return;
        _graphExecutor.UnbindBuffer(oldHandles.ProbePositions);
        _graphExecutor.UnbindBuffer(oldHandles.GridToProbeIndex);
        _graphExecutor.UnbindBuffer(oldHandles.ProbeWorldKeys);
        _graphExecutor.UnbindBuffer(oldHandles.WorldProbeHash);
        _graphExecutor.UnbindBuffer(oldHandles.ProbeCounter);
        _graphExecutor.UnbindBuffer(oldHandles.ProbeDrawArgs);
        _graphExecutor.UnbindBuffer(oldHandles.ProbeStates);
        _graphExecutor.UnbindBuffer(oldHandles.ProbeUpdateQueue);
        _graphExecutor.UnbindBuffer(oldHandles.VolumeState);
        _graphExecutor.UnbindTexture(oldHandles.Irradiance);
        _graphExecutor.UnbindTexture(oldHandles.Visibility);
        _boundDDGIHandles = null;
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
            else if (_plan != null &&
                i < _plan.Passes.Length &&
                _plan.Passes[i] is IGpuWorkTimingSource timingSource &&
                timingSource.TryGetSubmittedUnitCount(
                    timingFrame,
                    out int completedUnitCount) &&
                completedUnitCount > 0)
            {
                _gpuWorkScheduler.RecordCompletedWork(
                    timingSource.WorkDomain,
                    milliseconds,
                    completedUnitCount);
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
                    : access.Resource == VisibilityIdentifiersHandle
                        ? (ulong)_depthWidth * _depthHeight * 8ul
                    : access.Resource == VisibilityBarycentricsHandle
                        ? (ulong)_depthWidth * _depthHeight * 4ul
                    : access.Resource == VisibilityReconstructionHandle
                        ? (ulong)_depthWidth * _depthHeight * 8ul
                    : access.Resource == VisibilityReferenceHandle
                        ? (ulong)_depthWidth * _depthHeight * 8ul
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
            _graphExecutor.LastGpuTimingFrameNumber >= 0
                ? _graphExecutor.LastGpuTimingFrameNumber
                : _renderedFrameCount,
            cpuTotal,
            _graphExecutor.LastRawGpuFrameMilliseconds,
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
        AssertRenderThread();
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
                _contentRoot,
                this);
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
        if (handle == VisibilityIdentifiersHandle)
            return "Visibility Identifiers";
        if (handle == VisibilityBarycentricsHandle)
            return "Visibility Barycentrics";
        if (handle == VisibilityReconstructionHandle)
            return "Visibility Reconstruction";
        if (handle == VisibilityReferenceHandle)
            return "Visibility Raster Reference";
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

        ulong bytesPerPixel = RhiTexture.GetUncompressedBytesPerPixel(
            declaration.Texture.Format);
        if (bytesPerPixel == 0)
            bytesPerPixel = 4;
        return (ulong)declaration.Texture.Width *
            declaration.Texture.Height *
            bytesPerPixel;
    }

    public ulong Pick(uint x, uint y, uint w, uint h)
    {
        AssertRenderThread();
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
        AssertRenderThread();
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
        AssertRenderThread();
        DisposeRenderPlans();
        _loader = null;
        _depthTexture?.Dispose();
        _depthTexture = null;
        _outlineMaskTexture?.Dispose();
        _outlineMaskTexture = null;
        _visibilityIdentifiersTexture?.Dispose();
        _visibilityIdentifiersTexture = null;
        _visibilityBarycentricsTexture?.Dispose();
        _visibilityBarycentricsTexture = null;
        _visibilityReconstructionTexture?.Dispose();
        _visibilityReconstructionTexture = null;
        _visibilityReferenceTexture?.Dispose();
        _visibilityReferenceTexture = null;
        _graphExecutor.Dispose();
        _shadowAtlasPreviewRenderer?.Dispose();
        _shadowAtlasPreviewRenderer = null;
        _sharedBindlessHeap?.Dispose();
        _sharedBindlessHeap = null!;
        _compileCache?.Dispose();
        _compileCache = null!;
        EditorShaderBridge.ActiveShaderContextChanged -= OnActiveShaderContextChanged;
        if (s_active == this)
            s_active = null;
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
