// SPDX-License-Identifier: MIT
// Renderer-plugin host contract surface. Lives in Engine.RenderGraph so
// self-contained renderer plugins can implement IRendererPlanPlugin
// and read the host context without taking a hard dependency on
// Engine.Renderer.
//
// Plugin decoupling contract:
//   * Plugins read `context.Device`, `context.BindlessHeap`,
//     `context.Scene`, `context.ShaderCliArgs`, etc. for RHI and
//     scene data.
//   * Plugins get the process-wide shader compile cache via
//     `context.SharedShaderCache` (relocated from Renderer).
//   * Plugins implementing `IDDGIAtlasProvider` register via
//     `DDGIAtlasProviderRegistry.Register(...)` and the canonical
//     clustered plan consults the registry for atlas bindings, so
//     DDGI-specific code never needs to reach Engine.Renderer.
//   * The optional `context.Renderer` field is intentionally
//     nullable and is consumed ONLY by the path-tracing plugin
//     today; new plugins should prefer the renderer-free surface.

using Engine.RHI;
using Engine.Scene;
using System;
using Engine.Assets;
using Engine.RenderGraph.Shaders;
using System.Collections.Generic;

namespace Engine.RenderGraph;

/// <summary>Process-wide host services exposed to a renderer plugin's
/// <see cref="IRendererPlanPlugin.BuildPlan"/>.</summary>
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

    /// <summary>Optional host renderer reference. Typed as
    /// <see cref="object"/> to keep the project graph between
    /// <c>Engine.RenderGraph</c> and <c>Engine.Renderer</c>
    /// acyclic — <c>Engine.Renderer</c> depends on
    /// <c>Engine.RenderGraph</c> (it consumes <see cref="RenderPass"/>,
    /// <see cref="RenderGraphBuilder"/>, <see cref="IRendererPlanPlugin"/>
    /// + the executor types) but never the inverse. Plugins that
    /// need the full renderer surface cast via
    /// <c>context.Renderer as Engine.Renderer.Renderer</c> exactly
    /// once at the BuildPlan entry point. Self-contained plugins
    /// (DDGI, future authoring tools) read the renderer-free
    /// fields (<see cref="Device"/>, <see cref="BindlessHeap"/>,
    /// <see cref="SharedShaderCache"/>) instead and ignore this
    /// slot. May be null when no host renderer is wired yet.</summary>
    public object? Renderer { get; init; }

    /// <summary>Gets the GPU work scheduler for budget-aware admission.</summary>
    public required GpuWorkScheduler GpuWorkScheduler { get; init; }

    /// <summary>Gets whether shadow passes are enabled.</summary>
    public required bool RenderShadows { get; init; }

    /// <summary>Gets whether the sky pass is enabled.</summary>
    public required bool RenderSky { get; init; }

    /// <summary>Optional ordered Slang CLI argv tokens (e.g. ["-D",
    /// "DDGI_PLUGIN=1"]) gathered from enabled plugin manifests'
    /// <c>shader_features</c>. Plugins pass this verbatim into
    /// <c>RhiShader.FromSource(... includeDirs, cliArgs)</c> so
    /// host shaders can gate plugin-shader override paths.</summary>
    public IReadOnlyList<string>? ShaderCliArgs { get; init; }

    /// <summary>Optional ordered Slang <c>-I</c> include-path list
    /// merged from enabled plugins' <c>shader_includes</c> via
    /// <c>ShaderIncludeResolver.Resolve</c>, with the engine's
    /// <c>contentRoot/shaders</c> appended last as the default
    /// fallback. Plugins thread this into
    /// <c>RhiShader.FromSource(...)</c> so host shaders (e.g.
    /// <c>pbr.slang</c>) pull plugin-shipped include files
    /// (<c>ddgi_sampling.slang</c>, etc.) without forking the host
    /// source.</summary>
    public IReadOnlyList<string>? ShaderIncludeDirs { get; init; }

    /// <summary>Process-wide shader compile cache. Plugins and
    /// passes thread shader compilations through
    /// <see cref="ShaderCompileCache.GetOrCompileHash"/> so toggling
    /// plugins that don't actually change a shader's source bytes
    /// return the existing compiled <see cref="Engine.RHI.RhiShader"/>
    /// handle instead of forcing a Slang recompile + Metal pipeline
    /// state recreation. Replaces
    /// <c>Renderer.ShaderCompileCache</c> for plugin-side access so
    /// the renderer can shrink its public surface area.</summary>
    public ShaderCompileCache? SharedShaderCache { get; init; }
}

/// <summary>
/// Contains renderer-specific passes and persistent plan resources.
/// </summary>
public sealed class RendererPluginPlan
{
    /// <summary>Renderer-specific passes scheduled by this plugin.
    /// Plugins append via <see cref="AddPass"/> while the host
    /// executor iterates it post-BuildPlan. Public so plugin
    /// authors in different assemblies can schedule passes
    /// without taking a hard dependency on internals.</summary>
    public List<RenderPass> Passes { get; } = [];

    /// <summary>Append a renderer-owned pass to the plan. Plugins
    /// should call this from <see cref="IRendererPlanPlugin.BuildPlan"/>;
    /// the host executor iterates the list in insertion order.</summary>
    public void AddPass(RenderPass pass)
    {
        if (pass == null) throw new ArgumentNullException(nameof(pass));
        Passes.Add(pass);
    }
    public object? RasterSceneCache { get; set; }
    public object? DirectionalShadowState
    {
        get;
        set;
    }
    public object? DirectionalShadowPass
    {
        get;
        set;
    }
    public object? PunctualShadowState
    {
        get;
        set;
    }
    public object? PunctualShadowPass
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

    /// <summary>Builds renderer-specific passes and persistent
    /// resources. Plugins that opt to remain renderer-free should
    /// consume only the renderer-free properties of
    /// <paramref name="context"/>:
    /// <see cref="RendererPluginContext.Device"/>,
    /// <see cref="RendererPluginContext.BindlessHeap"/>,
    /// <see cref="RendererPluginContext.SharedShaderCache"/>,
    /// <see cref="RendererPluginContext.Scene"/>,
    /// <see cref="RendererPluginContext.GpuWorkScheduler"/>, etc.</summary>
    RendererPluginPlan BuildPlan(
        RendererPluginContext context);
}
