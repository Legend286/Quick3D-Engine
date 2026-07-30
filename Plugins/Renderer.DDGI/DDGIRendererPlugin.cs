// SPDX-License-Identifier: MIT
using System.Collections.Generic;
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
public sealed class DDGIRendererPlugin : IRendererPlanPlugin
{
    public string Id => "renderer.ddgi";

    public RendererPluginPlan BuildPlan(RendererPluginContext context)
    {
        return new RendererPluginPlan
        {
            Passes = new List<RenderPass>(),
        };
    }
}
