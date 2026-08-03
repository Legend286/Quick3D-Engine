// SPDX-License-Identifier: MIT
using Engine.Plugins;
using Engine.Renderer;
using Engine.RenderGraph;
using Engine.RHI;

namespace Engine.Plugin.Renderer.Clustered;

/// <summary>Registers the clustered visibility-buffer renderer module.</summary>
public sealed class ClusteredRendererPlugin :
    IEnginePlugin,
    IRendererPlanPlugin
{
    /// <inheritdoc />
    public string Id => "core.renderer.clustered";

    /// <inheritdoc />
    public void Initialize(IEnginePluginHost host)
    {
    }

    /// <inheritdoc />
    public void Shutdown()
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Shutdown();
    }

    /// <inheritdoc />
    public RendererPluginPlan BuildPlan(
        RendererPluginContext context)
    {
        var result = new RendererPluginPlan();
        Engine.RenderGraph.IDDGIAtlasProvider? ddgiProvider =
            context.EnableGlobalExtensions
                ? Engine.RenderGraph.DDGIAtlasProviderRegistry.Active
                : null;
        var cliArgs = context.ShaderCliArgs;
        var includeDirs = context.ShaderIncludeDirs;
        // Single cast at the BuildPlan entry point. Future plugins
        // that need the renderer surface adopt the same pattern;
        // self-contained plugins (DDGI, future authoring tools)
        // dereference the renderer-free fields instead.
        var renderer = (Engine.Renderer.Renderer)context.Renderer!;

        // Build the raster cache eagerly as a typed local so every
        // downstream pass ctor carries the strongly-typed reference.
        // The plan's RasterSceneCache slot stays `object?` to keep
        // Engine.RenderGraph free of any forward-reference to
        // Engine.Renderer; the assignment is an implicit upcast that
        // boxes the reference into the plan slot.
        var rasterCache =
            new RasterSceneGpuCache(
                context.Device,
                context.World,
                context.Scene,
                context.BindlessHeap,
                renderer);
        result.RasterSceneCache = rasterCache;

        DirectionalShadowState? dirShadowState = null;
        DirectionalShadowPass? dirShadowPass = null;
        PunctualShadowState? punctualShadowState = null;
        PunctualShadowPass? punctualShadowPass = null;

        if (context.RenderShadows &&
            context.Scene.Passes.Count > 0)
        {
            dirShadowState =
                new DirectionalShadowState(
                    context.Device,
                    context.BindlessHeap);
            result.DirectionalShadowState = dirShadowState;

            dirShadowPass =
                new DirectionalShadowPass(
                    context.Device,
                    context.ContentRoot,
                    rasterCache,
                    dirShadowState,
                    context.GpuWorkScheduler,
                    (context.Renderer as Engine.Renderer.Renderer)!);
            result.DirectionalShadowPass = dirShadowPass;

            punctualShadowState =
                new PunctualShadowState(
                    context.Device,
                    dirShadowState.Atlas,
                    context.BindlessHeap);
            result.PunctualShadowState = punctualShadowState;

            punctualShadowPass =
                new PunctualShadowPass(
                    context.Device,
                    context.ContentRoot,
                    rasterCache,
                    punctualShadowState,
                    context.GpuWorkScheduler,
                    (context.Renderer as Engine.Renderer.Renderer)!);
            result.PunctualShadowPass = punctualShadowPass;
        }

        var pbrPasses = new List<PbrPass>();
        IEnumerable<Engine.Scene.ScenePass> opaquePasses =
            context.EnableVisibilityBuffer
                ? context.Scene.Passes.Take(1)
                : context.Scene.Passes;
        foreach (var scenePass in opaquePasses)
        {
            pbrPasses.Add(
                new PbrPass(
                    context.Device,
                    scenePass,
                    context.ContentRoot,
                    context.BindlessHeap,
                    rasterCache,
                    dirShadowState,
                    punctualShadowState,
                    context.RenderSky,
                    cliArgs,
                    includeDirs,
                    ddgiProvider,
                    context.EnableVisibilityBuffer));
        }
        foreach (PbrPass pbrPass in pbrPasses)
            result.AddPass(pbrPass.CreateComputePass());
        if (dirShadowPass != null)
            result.AddPass(dirShadowPass);
        if (punctualShadowPass != null)
            result.AddPass(punctualShadowPass);
        if (context.EnableVisibilityBuffer && pbrPasses.Count > 0)
        {
            result.AddPass(pbrPasses[0].CreateVisibilityBufferPass());
            result.AddPass(
                pbrPasses[0].CreateVisibilityShadingPass());
        }
        else
        {
            result.Passes.AddRange(pbrPasses);
        }

        // DDGI probe overlay is editor-UI territory — the
        // ClusteredRendererPlugin doesn't own an IEnginePluginHost
        // (active when the editor wires renderer plugins into its
        // catalog at boot), so injecting the DDGIDebugPass here
        // would tie the runtime render path back to editor
        // services. The editor's PluginsWindow Show Probes
        // toggle wires the overlay via a sibling path; see
        // docs/editor/tools.md for the wiring contract.
        return result;
    }

}
