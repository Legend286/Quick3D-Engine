// SPDX-License-Identifier: MIT
using Engine.Plugins;
using Engine.Renderer;
using Engine.RHI;

namespace Engine.Plugin.Renderer.Clustered;

/// <summary>Registers the required clustered Forward+ renderer module.</summary>
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
        var cliArgs = context.ShaderCliArgs;
        var includeDirs = context.ShaderIncludeDirs;
        result.RasterSceneCache =
            new RasterSceneGpuCache(
                context.Device,
                context.World,
                context.Scene,
                context.BindlessHeap,
                context.Renderer);

        if (context.RenderShadows &&
            context.Scene.Passes.Count > 0)
        {
            result.DirectionalShadowState =
                new DirectionalShadowState(
                    context.Device,
                    context.BindlessHeap);
            result.DirectionalShadowPass =
                new DirectionalShadowPass(
                    context.Device,
                    context.ContentRoot,
                    result.RasterSceneCache,
                    result.DirectionalShadowState,
                    context.GpuWorkScheduler);
            result.PunctualShadowState =
                new PunctualShadowState(
                    context.Device,
                    result.DirectionalShadowState.Atlas,
                    context.BindlessHeap);
            result.PunctualShadowPass =
                new PunctualShadowPass(
                    context.Device,
                    context.ContentRoot,
                    result.RasterSceneCache,
                    result.PunctualShadowState,
                    context.GpuWorkScheduler);
        }

        var pbrPasses = new List<PbrPass>();
        foreach (var scenePass in
                 context.Scene.Passes)
        {
            pbrPasses.Add(
                new PbrPass(
                    context.Device,
                    scenePass,
                    context.ContentRoot,
                    context.BindlessHeap,
                    result.RasterSceneCache,
                    result.DirectionalShadowState,
                    result.PunctualShadowState,
                    context.RenderSky,
                    cliArgs,
                    includeDirs,
                    context.Renderer.ShaderCompileCache));
        }

        foreach (PbrPass pass in pbrPasses)
            result.Passes.Add(pass.CreateComputePass());
        if (result.DirectionalShadowPass != null)
            result.Passes.Add(
                result.DirectionalShadowPass);
        if (result.PunctualShadowPass != null)
            result.Passes.Add(
                result.PunctualShadowPass);
        result.Passes.AddRange(pbrPasses);

        // Plugin-shared debug overlays run AFTER Pbr so probes are
        // overlay-drawn on the populated scene rather than wiped by
        // Pbr's `BeginRenderPass(LoadOp.Clear)`. The DDGI plugin owns
        // the `ShowProbes` toggle on its static registry; the
        // canonical clustered plan only consults it here so the host
        // `ViewportDebugView` enum stays plugin-feature-free.
        Engine.DDGI.DDGIProbeVolume? ddgiVolume =
            Engine.DDGI.DDGIVolumeRegistry.ActiveVolume;
        if (ddgiVolume != null &&
            Engine.DDGI.DDGIVolumeRegistry.ShowProbes)
        {
            result.Passes.Add(
                new Engine.DDGI.DDGIDebugPass(
                    context.Device,
                    ddgiVolume,
                    context.Renderer,
                    context.ContentRoot,
                    cliArgs,
                    includeDirs,
                    context.Renderer.ShaderCompileCache));
        }
        return result;
    }
}
