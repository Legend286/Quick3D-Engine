// SPDX-License-Identifier: MIT
using Engine.Plugins;
using Engine.Renderer;

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
                    cliArgs));
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
        return result;
    }
}
