// SPDX-License-Identifier: MIT
using System;
using System.Numerics;

namespace Engine.RHI;

/// <summary>Identifies the renderer active in an editor viewport.</summary>
public enum ViewportRendererMode
{
    /// <summary>Clustered Forward+ raster rendering.</summary>
    Raster,

    /// <summary>Progressive path-traced rendering.</summary>
    PathTracing
}

/// <summary>Identifies the camera projection used by an editor viewport.</summary>
public enum ViewportProjectionMode
{
    /// <summary>Perspective projection using the camera field of view.</summary>
    Perspective,

    /// <summary>Orthographic projection using the viewport orthographic size.</summary>
    Orthographic
}

/// <summary>Identifies the visualization presented by an editor viewport.</summary>
public enum ViewportDebugView
{
    /// <summary>Displays the fully shaded scene.</summary>
    Lit,

    /// <summary>Displays triangle edges without filled surfaces.</summary>
    Wireframe,

    /// <summary>Displays camera depth using a false-colour gradient.</summary>
    Depth,

    /// <summary>Displays interpolated geometric normals.</summary>
    VertexNormal,

    /// <summary>Displays the final surface normal after normal mapping.</summary>
    PixelNormal,

    /// <summary>Displays diffuse base colour without lighting.</summary>
    Albedo,

    /// <summary>Displays ambient occlusion, roughness, and metallic channels.</summary>
    Rma,

    /// <summary>Displays illumination with diffuse colour removed.</summary>
    LightingOnly,

    /// <summary>Displays a repeating world-position visualization.</summary>
    WorldPosition,

    /// <summary>Displays material emissive output.</summary>
    Emissive,

    /// <summary>Displays surface texture coordinates.</summary>
    Uv,

    /// <summary>Displays the surface tangent direction.</summary>
    Tangent,

    /// <summary>Displays the surface bitangent direction.</summary>
    Bitangent
}

/// <summary>Identifies the active editor transform gizmo operation.</summary>
public enum ViewportGizmoOperation
{
    /// <summary>Moves the selected entity.</summary>
    Translate,

    /// <summary>Rotates the selected entity.</summary>
    Rotate,

    /// <summary>Scales the selected entity.</summary>
    Scale
}

/// <summary>Identifies the coordinate space used by the transform gizmo.</summary>
public enum ViewportGizmoSpace
{
    /// <summary>Uses the selected entity's local axes.</summary>
    Local,

    /// <summary>Uses world axes.</summary>
    World
}

public interface IGameLoop : IDisposable
{
    void Init(IntPtr deviceHandle, IntPtr swapchainHandle, IEntityStore world, bool enableImGui = true);
    void LoadScene(string contentRoot, string sceneName);
    void Update(InputState input);
    void RenderFrame(RhiTexture backBuffer, uint width, uint height);
    void RenderThumbnail(string contentRoot, string assetPath, string assetType, RhiTexture target, uint width = 256, uint height = 256, float orbitRadians = 0.0f, int modelPartIndex = -1, RhiFence? syncFence = null, ulong waitValue = 0, ulong signalValue = 0);
    void LoadModelPreview(string contentRoot, string modelPath, int modelPartIndex = -1);
    void LoadMaterialPreview(string contentRoot, string materialPath, bool usePathTracer = true);
    void RenderLoadedPreview(RhiTexture target, uint width = 256, uint height = 256, float orbitRadians = 0.0f, RhiFence? syncFence = null, ulong waitValue = 0, ulong signalValue = 0);
    bool RenderShadowAtlasTilePreview(ulong entityId, int faceIndex, bool dynamicTile, RhiTexture target, uint width = 512, uint height = 512, RhiFence? syncFence = null, ulong waitValue = 0, ulong signalValue = 0);
    void UpdateMaterialPreview(float[] albedo, float metallic, float roughness, float subsurface, float[] subsurfaceColor, float[] subsurfaceRadius, float clearcoat, float clearcoatRoughness, float[] topColor, float topMetallic, float topRoughness, uint topMaskType, float noiseScale = 10.0f, float noiseThresholdMin = 0.3f, float noiseThresholdMax = 0.7f, float[]? layer2Color = null, float layer2Metallic = 0.0f, float layer2Roughness = 1.0f, uint layer2MaskType = 0, float layer2NoiseScale = 10.0f, float layer2NoiseMin = 0.3f, float layer2NoiseMax = 0.7f);
    ulong AddDirectionalLight(Vector3 direction, Vector3 color, float intensity, float angularRadius, bool castShadows = true);
    ulong AddPointLight(Vector3 position, Vector3 color, float intensity, float range, float sourceRadius, bool castShadows = true);
    ulong AddSpotLight(Vector3 position, Vector3 direction, Vector3 color, float intensity, float range, float innerCone, float outerCone, float sourceRadius, bool castShadows = true);
    void ReplaceSwapchain(RhiSwapchain swapchain);
    void SetSelectedEntity(ulong entityId);

    /// <summary>Gets or sets the active viewport renderer.</summary>
    ViewportRendererMode RendererMode { get; set; }

    /// <summary>Gets or sets the active viewport camera projection.</summary>
    ViewportProjectionMode ProjectionMode { get; set; }

    /// <summary>Gets or sets the active viewport visualization.</summary>
    ViewportDebugView DebugView { get; set; }

    /// <summary>Gets or sets the editor camera vertical field of view.</summary>
    float CameraFieldOfViewDegrees { get; set; }

    /// <summary>Gets or sets the editor camera orthographic vertical size.</summary>
    float OrthographicSize { get; set; }

    /// <summary>Gets or sets the active transform gizmo operation.</summary>
    ViewportGizmoOperation GizmoOperation { get; set; }

    /// <summary>Gets or sets the transform gizmo coordinate space.</summary>
    ViewportGizmoSpace GizmoSpace { get; set; }

    /// <summary>Gets or sets whether transform gizmo snapping is enabled.</summary>
    bool GizmoSnapping { get; set; }

    /// <summary>Gets or sets whether the optional path-tracing plugin is available.</summary>
    bool IsPathTracingRendererAvailable { get; set; }

    /// <summary>Recreates pipelines whose shaders are owned by a plugin.</summary>
    void ReloadPluginShaders(string pluginId);

    /// <summary>Invoked during the ImGui viewport overlay pass to draw plugin-contributed ImGui.</summary>
    Action<InputState, uint, uint>? DrawPluginOverlay { get; set; }

    /// <summary>Reloads a managed plugin assembly in its collectible context.</summary>
    void ReloadPluginCode(string pluginId);
    void ApplyMaterialToSubmesh(uint x, uint y, uint w, uint h, string materialPath);
    RenderGraphDiagnosticsSnapshot? GetRenderGraphDiagnostics();
    event Action<ulong>? OnEntityPicked;

    /// <summary>Occurs when the active viewport renderer changes.</summary>
    event Action<ViewportRendererMode>? RendererModeChanged;

    /// <summary>Occurs when the viewport camera projection changes.</summary>
    event Action<ViewportProjectionMode>? ProjectionModeChanged;

    /// <summary>Occurs when the viewport visualization changes.</summary>
    event Action<ViewportDebugView>? DebugViewChanged;

    /// <summary>Occurs when a transform gizmo drag begins.</summary>
    event Action<ulong>? EntityTransformEditStarted;

    /// <summary>Occurs when a transform gizmo drag completes.</summary>
    event Action<ulong>? EntityTransformEditCompleted;

    /// <summary>Occurs when an unavailable renderer plugin is requested.</summary>
    event Action<string>? RendererPluginEnableRequested;
}
