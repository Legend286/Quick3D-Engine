// SPDX-License-Identifier: MIT
using Engine.Plugins;
using Engine.Renderer;

namespace Engine.DDGI;

/// <summary>
/// Minimal DDGI plugin entry point demonstrating the modular shader
/// plumbing. Returns an empty render plan; the value-add is the
/// manifest declarations (shader_includes + shader_features) that the
/// renderer honours automatically, plus the host pbr.slang's
/// #ifdef DDGI_PLUGIN / #include "ddgi_sampling.slang" override path
/// that engages when this plugin is enabled.
/// </summary>
public sealed class DDGIRendererPlugin :
    IEnginePlugin,
    IRendererPlanPlugin
{
    /// <inheritdoc />
    public string Id => "renderer.ddgi";

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
        RendererPluginContext context) => new();
}
