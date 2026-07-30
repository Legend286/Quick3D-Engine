// SPDX-License-Identifier: MIT
using Engine.Plugins;

namespace Engine.Plugin.SurfaceAuditor;

/// <summary>
/// Editor-only diagnostic plugin that crawls the engine's API surface and
/// surfaces contract/coverage gaps. See Plugins/SurfaceAuditor/plugin.json
/// for manifest metadata; full audit logic lands in follow-up commits.
/// </summary>
public sealed class SurfaceAuditorPlugin : IEditorPlugin
{
    private IEditorPluginHost? _host;

    /// <inheritdoc />
    public string Id => "core.diagnostics.surface-auditor";

    /// <inheritdoc />
    public void Initialize(IEnginePluginHost host)
    {
        // Editor-kind plugin: full setup lives in InitializeEditor so menu
        // actions and panel registrations wait for the Avalonia host.
    }

    /// <inheritdoc />
    public void InitializeEditor(IEditorPluginHost host)
    {
        _host = host;
        _host.RegisterMenuAction(
            Id,
            "Tools",
            "Run Surface Audit",
            OnRunSurfaceAuditRequested);
        Engine.CBindings.Log.Info(
            "[SurfaceAuditor] Loaded; Tools > Run Surface Audit registered.",
            "SurfaceAuditor");
    }

    /// <inheritdoc />
    public void Shutdown()
    {
        _host = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Shutdown();
    }

    private void OnRunSurfaceAuditRequested()
    {
        if (_host == null)
        {
            Engine.CBindings.Log.Warn(
                "[SurfaceAuditor] Run requested before host attached.",
                "SurfaceAuditor");
            return;
        }
        Engine.CBindings.Log.Info(
            $"[SurfaceAuditor] Running drift probe against engine root: {_host.EngineRoot}",
            "SurfaceAuditor");
        DriftProbe.Run(_host.EngineRoot);
    }
}
