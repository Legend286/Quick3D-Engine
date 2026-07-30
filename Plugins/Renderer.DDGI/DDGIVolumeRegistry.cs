// SPDX-License-Identifier: MIT
// Plugin-owned single-slot handle that exposes the active
// DDGIRendererPlugin's probe volume + debug-visualisation toggle
// to the rest of the engine. Lives inside Plugins/Renderer.DDGI
// so the plugin owns its own state surface instead of leaking
// DDGI-specific code into Engine.Renderer's namespace.
//
// Lifecycle:
//   1. DDGIRendererPlugin.Initialize() calls Register(this, _volume).
//   2. ClusteredRendererPlugin.BuildPlan reads ActiveVolume +
//      ShowProbes to decide whether to inject a DDGIDebugPass.
//   3. The editor's viewport toggle writes ShowProbes.
//   4. DDGIRendererPlugin.Shutdown() calls Unregister(this).
//
// Single-slot model — only one DDGIRendererPlugin instance is
// "active" at a time. Stacking DDGI plugins would require a
// Dictionary<pluginId, registry> + per-plugin ShowProbes flags.

namespace Engine.DDGI;

public static class DDGIVolumeRegistry
{
    private static readonly object _gate = new();
    private static IRendererPlanPluginLite? _activePlugin;
    private static DDGIProbeVolume? _activeVolume;
    private static bool _showProbes;

    /// <summary>Active DDGIRendererPlugin instance, or null when no
    /// DDGI plugin is loaded or it has shut down.</summary>
    public static IRendererPlanPluginLite? ActivePlugin
    {
        get { lock (_gate) { return _activePlugin; } }
    }

    /// <summary>Active probe volume, or null when <see cref="ActivePlugin"/>
    /// is null.</summary>
    public static DDGIProbeVolume? ActiveVolume
    {
        get { lock (_gate) { return _activeVolume; } }
    }

    /// <summary>Editor-driven toggle for the in-viewport probe
    /// marker overlay. The ClusteredRendererPlugin reads this to
    /// decide whether to inject <see cref="DDGIDebugPass"/>. Owned
    /// by the plugin so the host assemblies remain DDGI-free.</summary>
    public static bool ShowProbes
    {
        get { lock (_gate) { return _showProbes; } }
        set { lock (_gate) { _showProbes = value; } }
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
                // NOTE: `_showProbes` is deliberately preserved across
                // Unregister so the user's "DDGI Probes" toggle survives
                // a PluginCatalogService hot-reload of renderer.ddgi.
                // The VM's `_showDDGIProbes` ObservableProperty remains
                // the source of truth; Unregister is purely an
                // active-volume handshake, not a UX reset.
            }
        }
    }
}

/// <summary>Marker interface that lets the registry identify
/// registered plugin instances by identity without a hard
/// dependency on Engine.RenderGraph's IRendererPlanPlugin
/// machinery. Decoupling keeps the marker testable and the
/// registry bounds clear.</summary>
public interface IRendererPlanPluginLite
{
}
