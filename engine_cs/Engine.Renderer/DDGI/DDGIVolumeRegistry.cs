// SPDX-License-Identifier: MIT
// Lightweight process-wide handle that lets the canonical
// ClusteredRendererPlugin reach the active DDGIRendererPlugin's CPU
// probe volume during BuildPlan. The DDGI plugin registers itself
// here on Initialize; unregisters on Shutdown. Avoids the larger
// refactor of migrating every IRendererPlanPlugin into Renderer's
// primary dispatch path just for one debug overlay.
//
// Single-slot model — only one DDGIRendererPlugin instance can be
// "active" at a time. Multiple DDGI plugins stacked would require
// a Dictionary<pluginId, registry>, out of scope for commit 2.

using Engine.Renderer.DDGI;

namespace Engine.DDGI;

public static class DDGIVolumeRegistry
{
    private static readonly object _gate = new();
    private static IRendererPlanPluginLite? _activePlugin;
    private static DDGIProbeVolume? _activeVolume;

    /// <summary>Gets the active DDGIRendererPlugin instance, or
    /// null when no DDGI plugin is loaded or it has shut down.</summary>
    public static IRendererPlanPluginLite? ActivePlugin
    {
        get { lock (_gate) { return _activePlugin; } }
    }

    /// <summary>Gets the active probe volume, or null when
    /// <see cref="ActivePlugin"/> is null.</summary>
    public static DDGIProbeVolume? ActiveVolume
    {
        get { lock (_gate) { return _activeVolume; } }
    }

    public static void Register(
        IRendererPlanPluginLite plugin,
        DDGIProbeVolume volume)
    {
        lock (_gate)
        {
            _activePlugin = plugin;
            _activeVolume = volume;
        }
    }

    public static void Unregister(IRendererPlanPluginLite plugin)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activePlugin, plugin))
            {
                _activePlugin = null;
                _activeVolume = null;
            }
        }
    }
}

/// <summary>Narrowed interface so the registry can be consumed
/// without a hard dependency on Engine.RenderGraph's RenderPass
/// machinery — keeps the static handle testable and the plugin
/// boundary clean.</summary>
public interface IRendererPlanPluginLite
{
}
