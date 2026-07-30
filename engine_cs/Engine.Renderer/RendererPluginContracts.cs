// SPDX-License-Identifier: MIT
using Engine.RenderGraph;
using Engine.RHI;
using Engine.Scene;
using System.Collections.Generic;

namespace Engine.Renderer;

/// <summary>Provides host-owned rendering services to a renderer plugin.</summary>
public sealed class RendererPluginContext
{
    /// <summary>Gets the host-owned RHI device.</summary>
    public required RhiDevice Device { get; init; }

    /// <summary>Gets the active ECS world.</summary>
    public required IEntityStore World { get; init; }

    /// <summary>Gets the active scene graph.</summary>
    public required SceneGraph Scene { get; init; }

    /// <summary>Gets the project content root.</summary>
    public required string ContentRoot { get; init; }

    /// <summary>Gets the shared renderer bindless heap.</summary>
    public required RhiBindlessHeap BindlessHeap { get; init; }

    /// <summary>Gets the host renderer.</summary>
    public required Renderer Renderer { get; init; }

    public required GpuWorkScheduler GpuWorkScheduler
    {
        get;
        init;
    }

    public required bool RenderShadows { get; init; }

    public required bool RenderSky { get; init; }

    /// <summary>Optional ordered Slang CLI argv tokens (e.g. ["-D",
    /// "DDGI_PLUGIN=1"]) gathered from enabled plugin manifests' <c>shader_features</c>.
    /// Plugins pass this verbatim into the new
    /// <c>RhiShader.FromSource(... includeDirs, cliArgs)</c> overload so
    /// host shaders can gate plugin-shader override paths.</summary>
    public IReadOnlyList<string>? ShaderCliArgs { get; init; }
}

/// <summary>
/// Contains renderer-specific passes and persistent plan resources.
/// </summary>
public sealed class RendererPluginPlan
{
    internal List<RenderPass> Passes { get; } = [];
    internal RasterSceneGpuCache? RasterSceneCache { get; set; }
    internal DirectionalShadowState? DirectionalShadowState
    {
        get;
        set;
    }
    internal DirectionalShadowPass? DirectionalShadowPass
    {
        get;
        set;
    }
    internal PunctualShadowState? PunctualShadowState
    {
        get;
        set;
    }
    internal PunctualShadowPass? PunctualShadowPass
    {
        get;
        set;
    }
}

/// <summary>Builds a renderer-owned render-graph plan.</summary>
public interface IRendererPlanPlugin
{
    /// <summary>Gets the stable renderer plugin identifier.</summary>
    string Id { get; }

    /// <summary>Builds renderer-specific passes and persistent resources.</summary>
    RendererPluginPlan BuildPlan(
        RendererPluginContext context);
}
