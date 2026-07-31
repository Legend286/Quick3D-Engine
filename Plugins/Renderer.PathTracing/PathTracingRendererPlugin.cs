// SPDX-License-Identifier: MIT
using Engine.Plugins;
using Engine.RenderGraph;
using Engine.Renderer;
using Engine.Scene;

namespace Engine.Plugin.Renderer.PathTracing;

/// <summary>Registers the optional path-tracing renderer module.</summary>
public sealed class PathTracingRendererPlugin :
    IEnginePlugin,
    IRendererPlanPlugin
{
    /// <inheritdoc />
    public string Id => "core.renderer.path-tracing";

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
        var renderer = (Engine.Renderer.Renderer)context.Renderer!;
        foreach (ScenePass scenePass in
                 context.Scene.Passes)
        {
            result.Passes.Add(
                new PathTracerPass(
                    context.Device,
                    context.World,
                    context.Scene,
                    scenePass,
                    context.ContentRoot,
                    context.BindlessHeap,
                    renderer));
        }
        return result;
    }
}
