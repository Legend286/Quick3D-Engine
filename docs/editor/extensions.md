# Editor Extensions

Editor extensions are managed plugins. `Editor.Services.PluginCatalogService`
discovers them at editor start by enumerating `Plugins/*/plugin.json` and
loading each assembly into its own `AssemblyLoadContext` per the host wiring
in `engine_cs/Engine.Renderer.RendererPluginRuntime`. Each plugin's
manifest `kind` selects the contract surface it implements.

## Contract surface per kind

| Kind | Contract | Defined in | Host surface |
| --- | --- | --- | --- |
| `Editor` | `IEditorPlugin` | `engine_cs/Engine.Plugins/PluginContracts.cs` | `IEditorPluginHost.RegisterMenuAction` / `RegisterImGuiOverlay` / `RegisterToolPanel` |
| `Renderer` | `IRendererPlanPlugin` | `engine_cs/Engine.Renderer/RendererPluginContracts.cs` | `RendererPluginContext` (Device / World / Scene / ContentRoot / BindlessHeap / Renderer / GpuWorkScheduler / RenderShadows / RenderSky) |
| `Runtime` | `IEnginePlugin` | `engine_cs/Engine.Plugins/PluginContracts.cs` | `IEnginePluginHost.{EngineRoot,ProjectRoot}` + `InvalidatePluginShaders` |
| `AssetPipeline` | _reserved_ | _no contract yet_ | _none — flag surfaced by SurfaceAuditor below_ |

Adding a new engine-owned plugin requires updating FOUR hardcoded allow-lists;
`core.diagnostics.surface-auditor` (below) audits that drift.

## Bundled plugins

### core.renderer.clustered — `Engine.Plugin.Renderer.Clustered`

Required clustered Forward+ raster renderer. Defines
`ClusteredRendererPlugin.BuildPlan` which constructs the raster scene cache,
directional + punctual shadow passes, and a per-scene-pass Forward+ pass list.
Manifest in `Plugins/Renderer.Clustered/plugin.json`.

### core.renderer.path-tracing — `Engine.Plugin.Renderer.PathTracing`

Optional path-tracing renderer. Defines `PathTracingRendererPlugin.BuildPlan`
which builds a per-scene-pass `PathTracerPass` list. Plugin DLL is opt-in
via the editor's Plugins window (`core.renderer.path-tracing` checkbox).
Manifest in `Plugins/Renderer.PathTracing/plugin.json`.

### core.diagnostics.surface-auditor — `Engine.Plugin.SurfaceAuditor`

Editor-only diagnostic plugin. Registers **Tools > Run Surface Audit** via
`IEditorPluginHost.RegisterMenuAction`. Triggering it invokes
`DriftProbe.Run(_host.EngineRoot)` which scans the four hardcoded engine
assembly allow-lists and emits per-name `Warn` log lines for any known
`Engine.*` name absent from one or more sources. Manifest in
`Plugins/SurfaceAuditor/plugin.json`; see `Plugins/SurfaceAuditor/DriftProbe.cs`
for the audited source list.

## Adding a new plugin

1. Create `Plugins/<YourPlugin>/` containing:
   - `<YourPlugin>.csproj` referencing `engine_cs/Engine.Plugins/Engine.Plugins.csproj`.
     Add `OutOfBand/Engine.CBindings/Engine.CBindings.csproj` if you need to log.
   - `plugin.json` following the schema documented in
     `engine_cs/Engine.Plugins/EnginePluginManifest`.
   - One C# entry-point class implementing the kind's contract.
2. Append your plugin directory to the `for PLUGIN_DIR in …` loop in
   `scripts/build-mac-app.sh` so it ships in the .app bundle.
3. Update this file with a per-plugin entry describing its menu/overlay/panel
   surface and what audit probe covers it (so `core.diagnostics.surface-auditor`
   can flag if your plugin's allow-list drifts).
4. Run `dotnet build Plugins/<YourPlugin>/…csproj -c Release` to verify
   before staging the change.
