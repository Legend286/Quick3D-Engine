# Editor Extensions

Renderer-extension unload removes its passes and reconstructs the render plan
before shutdown releases plugin-owned GPU resources. Both operations execute as
one renderer-owner transaction, and only afterward does the editor unload the
managed assembly context. An active pass therefore cannot retain a buffer or
texture that plugin shutdown has already released, and a rebuilt clustered pass
cannot capture a provider whose resources are being torn down.

Renderer-extension enable registers with an existing renderer immediately,
before shader-feature notifications rebuild cached plans. Registration does not
require an active scene; the next scene plan includes every enabled extension.

Editor extensions are managed plugins. `Editor.Services.PluginCatalogService`
discovers them at editor start by enumerating `Plugins/*/plugin.json` and
loading each assembly into its own `AssemblyLoadContext` per the host wiring
in `engine_cs/Engine.Renderer.RendererPluginRuntime`. Each plugin's
manifest `kind` selects the contract surface it implements.

When loose plugin outputs exist in more than one configuration, the catalog
loads the newest matching assembly rather than preferring Debug or Release by
directory order. This prevents an older output from being paired with newer
host contracts after switching build configurations.

Renderer extensions are activated through the active renderer's owner-thread
queue. File watchers and build continuations can discover binaries on workers,
but render-plan mutation, shader and pipeline creation, GPU allocation, and
plan disposal execute only on the thread that owns the viewport renderer. The
queue drains before render-graph execution.

The same ownership rule applies to viewport picking, scene and preview-plan
construction, hover previews, shadow-atlas previews, and static thumbnail
generation. The editor currently drives rendering from the Avalonia dispatcher,
so that dispatcher is the renderer owner. Background tasks are limited to CPU,
filesystem, process, and shader-source preparation work.

Offscreen thumbnail and preview renderers do not register as the process-wide
active viewport and do not consume renderer-extension providers. Consequently,
creating a thumbnail cannot redirect extension activation, unload, camera
queries, or DDGI atlas binding away from the interactive scene renderer.

## Contract surface per kind

| Kind | Contract | Defined in | Host surface |
| --- | --- | --- | --- |
| `Editor` | `IEditorPlugin` | `engine_cs/Engine.Plugins/PluginContracts.cs` | `IEditorPluginHost.RegisterMenuAction` / `RegisterImGuiOverlay` / `RegisterToolPanel` |
| `Renderer` | `IRendererPlanPlugin` | `engine_cs/Engine.Renderer/RendererPluginContracts.cs` | `RendererPluginContext` (Device / World / Scene / ContentRoot / BindlessHeap / Renderer / GpuWorkScheduler / RenderShadows / RenderSky / EnableGlobalExtensions) |
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

Editor-only diagnostic plugin. Registers **Tools > Extensions > Run Surface Audit**
via `IEditorPluginHost.RegisterMenuAction`. Triggering it invokes
`DriftProbe.Run(_host.EngineRoot)` which scans the four hardcoded engine
assembly allow-lists and emits per-name `Warn` log lines for any known
`Engine.*` name absent from one or more sources. Manifest in
`Plugins/SurfaceAuditor/plugin.json`; see `Plugins/SurfaceAuditor/DriftProbe.cs`
for the audited source list.

## Menu integration

The editor plugin contract surfaces into viewport and tool UI backends through the shared
`Editor.Services.DynamicMenuService`, which `Editor.Services.PluginCatalogService`
writes to as the host.

| Plugin call | Editor surface | Status |
| --- | --- | --- |
| `RegisterMenuAction(id, path, name, onExecute)` | Avalonia `Tools > Extensions` submenu | wired |
| `RegisterImGuiOverlay(id, onDraw)` | viewport ImGui overlay | registered, render-pipeline wiring deferred |
| `RegisterToolPanel(id, title, control)` | dockable panel | registered, dock wiring deferred |
| `RegisterDebugView(id, name, onToggle)` | viewport debug picker | wired |
| `RegisterDebugViewToggle(id, view, name, initial, onToggle)` | checkbox inside the named debug view's picker section | wired |

Debug-view toggles are scoped to their owning view and disappear when another
visualization is selected. Their state belongs to the plugin callback rather
than the editor, so renderer extensions can expose diagnostics without the host
naming plugin types.

`DynamicMenuService` retains the active debug-view name across plugin unload and
reload. A newly registered view immediately receives its active state, which
keeps an already-selected renderer visualization enabled after code reload.
The viewport keeps a stable observable view list and rejects transient empty
selections while plugin registrations refresh, so the picker always retains a
valid renderer mode.

### Menu actions (v1 contract)

`MainWindow` (`Editor/MainWindow.axaml.cs`) subscribes to
`DynamicMenuService.Shared.OnMenusChanged` once in its constructor — before
`Opened` fires — and calls `RebuildDynamicToolsMenu()` immediately so plugin
actions registered during `PluginCatalogService.Discover()` (which runs in
the catalog constructor, before MainWindow is even constructed) still
appear on the first paint.

The rebuild targets the `MenuItem` declared in `Editor/MainWindow.axaml` as
`<MenuItem x:Name="ExtensionsToolsMenuItem" Header="_Extensions" IsVisible="False" />`,
placed immediately under `_Tools`. The submenu is hidden when no plugin has
registered an action.

**v1 contract**: only `menuPath == "Tools"` is honoured — every registered
action appears as a flat entry under `Tools > Extensions`. Nested paths
(e.g. `"Tools > Lighting"`) are reserved for a future iteration. The
plugin's `ItemName` becomes the MenuItem header; clicking it invokes the
`onExecute` delegate inside a try/catch that routes any exception through
`EngineLog`.

`MainWindow.OnClosed` removes its `OnMenusChanged` subscription before
disposing `PluginCatalogService`, so a late `Unregister` cannot crash into
a disposed control tree.

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
