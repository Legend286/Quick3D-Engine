# Editor Tools

> **TODO(editor):** Material editor + Model viewer + Particle editor + Level editor + Asset browser. Per-tool feature list.

See [`engine-spec.md` §8.3](../../engine-spec.md).

## Panel lifecycle

The viewport panel's Metal init is driven from
`MainWindow.Opened`, NOT from `OnDataContextChanged` on the
View. `OnDataContextChanged` fires during XAML evaluation when
the View's data context is set, before the Avalonia visual
subtree is connected to a TopLevel - that timing aborts Metal
init with "Viewport host is not a Window". `Window.Opened`
fires after the window is fully shown AND its children have
been laid out, the first moment `TopLevel.GetTopLevel(this)`
returns a usable Window handle reliably. The matching teardown
runs from `MainWindow.OnClosed`'s `vm.ViewportVm.DisposeOnClose()`
call; `ViewportPanelView` is now a pure XAML renderer with no
lifecycle overrides of its own.

## Metal embed architecture

`Editor/Views/ViewportPanelView.axaml` hosts a
`ViewportMetalLayerHost` (an Avalonia 11
[`NativeControlHost`](https://docs.avaloniaui.net/docs/guides/advanced/native-controls)
subclass). On macOS the host's `CreateNativeControlCore`
override calls into the C RHI to allocate a child `NSView`
via `rhi_create_macos_metal_view` and returns an
`IPlatformHandle` wrapping that pointer. Avalonia's
`EmbeddableControlRoot` then composites the child `NSView`
into the visual tree as a discrete child visual rather than
overlaying its own Skia content over a contentView layer
replace (the previous "swap `win.contentView.layer`" trick,
which Avalonia's render timer clobbered back).

The C RHI consumes the `NSView*` from the host handle and
attaches a `CAMetalLayer` as a sublayer of the `NSView`'s
`layer` (`metal_create_swapchain` in `engine_c/rhi/rhi_metal.mm`).
`layer.drawableSize` matches the host's physical-pixel bounds
(rebuilt from the host bounds multiplied by the active
`RenderScaling` factor), and the sublayer's
`autoresizingMask` keeps it glued to the host's bounds when
Avalonia resizes. Passes encode against the next-drawable
texture acquired from the swapchain; submit and the new
drawable composites into the host's layer tree at the next
vsync.

`ViewportPanelViewModel` watches the host bounds every frame and
recreates the swapchain when either the logical size or the DPI
scale changes. That keeps `RenderFrame(width, height)` and the
outline/composite passes on the same physical pixel dimensions as
the visible viewport instead of the initial attach-time size.

`ViewportPanelView` frames the native host with separate scene/status and
navigation chrome rows. No Avalonia visual overlaps the native rectangle, so
the layout retains correct platform composition and pointer routing. The Metal
host layer uses continuous rounded clipping matching the inset render well;
the swapchain drawable size still derives only from the native host bounds and
active DPI scale.

Pointer events are transformed into `ViewportMetalLayerHost` coordinates
before they reach object picking or ImGui. The surrounding title, margin,
border, and status chrome therefore cannot offset render-surface input.
Drag-and-drop submesh picking additionally converts those logical host
coordinates to physical pixels with the active `RenderScaling`.

There is no `WriteableBitmap` + `Image` readback path
anymore on macOS - Metal draws straight to the embedded
NSView, so the Avl Skia round-trip is bypassed entirely.
`Engine.RHI/RhiTexture.Readback` is preserved for editor
preview screenshots (Phase 3+).

## Object Selection and Outlines

The Editor implements a hardware-accelerated object selection and outline rendering pipeline through three RenderGraph passes:

1. `IdPickingPass`: Renders the entity ID (`uint64_t`) into a dedicated texture format (`R32Uint`). The Editor reads back this texture at the cursor coordinate on mouse click to resolve selections in O(1) time regardless of scene complexity.
2. `OutlineMaskPass`: Generates a solid white 2D silhouette of the currently selected entity. This pass does not read or write depth, meaning the selection outline acts as an X-Ray overlay visible through scene geometry.
3. `OutlineCompositePass`: A post-processing pass that samples the `OutlineMaskPass` output texture using a cross-neighborhood edge detection shader. It renders an orange outline at the silhouette boundary directly onto the backbuffer.

The composite pass binds the current outline mask directly for each draw.
Viewport resize can therefore replace the mask without leaving a stale native
pointer in the shared bindless heap, and without mutating an argument buffer
that an earlier GPU frame may still be reading.

## Asset Thumbnails

Content-browser thumbnails now use three dedicated preview paths instead of the
main scene renderer defaults:

1. Model thumbnails render through the raster PBR pass with the sky disabled,
   a fixed preview light rig, no editor grid, and a camera fitted from the
   model's geometry-derived bounds sphere so yaw cannot clip the asset. Models
   occupy 78 percent of the limiting viewport dimension to retain readable
   breathing room.
2. Material thumbnails render a preview sphere with the same no-sky light rig
   and a bounds-derived camera placement targeting 90 percent viewport
   coverage. The live material preview uses the same FOV and fill ratio.
3. Texture thumbnails bypass scene lighting entirely and use a dedicated blit
   pass that samples the texture directly into the icon target.
4. The editor preserves already loaded thumbnail bitmaps across asset-list
   refreshes and reuses on-disk cache files immediately, so icons no longer
   disappear while unrelated thumbnails are being generated.
5. Thumbnail generation now runs through a single preview worker. The asset
   loaders and registry still use shared global caches, so serializing icon
   renders avoids GPU and asset-cache races when browsing heavy model folders.

Imported `.mdl` tiles expose an expansion control when parts are present.
Expanding inserts one `PART` tile per stable source index. Each child has its
own sphere-fitted icon and live preview. Drag payloads carry the source model
path plus optional part index: dropping the parent instantiates the complete
model, while dropping a child instantiates only that part. The part index is
stored in scene JSON so save and reload preserve the choice.

Pressing Import validates the request, enqueues it with the editor-wide asset
import service, and closes the import window immediately. Cooking and model
thumbnail generation run away from the UI thread. The main editor status bar
shows the active cook or thumbnail stage at bottom right. Completion never
loads a generated scene or instantiates the imported model into the active
scene.

Model import finishes with a separate `Generating thumbnails` progress stage.
It renders the whole-model icon and every stable part icon before reporting
success. Model and part tiles therefore use their static cache immediately
when the content browser observes the imported files.

## Project And Scene Navigation

The welcome screen remembers the parent directory of the last opened or
created project in the user's local Quick3D editor settings. Project folder
pickers and the new-project location field start there on later launches.
Writes use a flushed temporary file followed by an atomic replace.

Open Scene and Save Scene As start in the active project's `Content/scenes`
directory. Double-clicking a scene asset in the content browser opens it
through the main editor scene-loading path. Scene paths below nested
`Content/scenes` folders retain their relative directory component. Scene
tiles use a blue Material Icons landscape glyph so they remain identifiable
without generated thumbnails.

## Viewport Renderer Mode

The viewport title chrome exposes a PBR/path-tracing selector backed by
`IGameLoop.RendererMode`. The selected value is reapplied after scene loads and
game-assembly hot reloads. The `P` shortcut publishes the same mode-change
event, keeping the selector synchronized.

Raster and path tracing each retain a compiled render-plan state bundle for the
active scene. Returning to a previously used mode activates its cached pass
schedule and renderer-owned resources without invoking the graph compiler.
Scene topology changes and preview-plan changes invalidate both bundles.

## Viewport Camera And Debug Controls

The projection button in the viewport chrome opens the editor-camera controls.
Perspective and orthographic projection use one camera-data path across raster,
path tracing, picking, selection outlines, and editor overlays. Switching
projection animates a short matrix morph so the viewport retains spatial
context instead of snapping between projections. Perspective exposes a
15-120-degree vertical field-of-view slider; orthographic projection exposes
its vertical world-space size.

The Realtime toggle controls viewport presentation rather than simulation.
Realtime mode continuously acquires, renders, and presents swapchain images.
With Realtime disabled, the editor leaves the last frame resident and requests
short render bursts for camera input, selection, scene edits, resize, renderer
changes, projection transitions, and debug-view changes. This avoids continuous
GPU presentation while inspecting a static scene on battery-powered systems.

The debug selector is shared by PBR and path tracing. It currently exposes Lit,
Wireframe, Depth, Vertex Normal, Pixel Normal, Albedo, RMA, Lighting Only,
World Position, Emissive, UV, Tangent, and Bitangent. These modes are frame
state and do not rebuild the render graph. Path-traced debug changes reset
accumulation so samples from different visualizations cannot mix.

## Asset Hover Preview

The content browser now exposes a delayed hover-preview card for renderable
assets. Textures show an enlarged cached image preview inside the main editor
window. Models and materials switch to a live top-level preview window that
imports a shared GPU render target into Avalonia composition, instead of
scaling up a thumbnail bitmap or spinning up a second viewport host.

The live preview surface uses the same engine renderer path as the rest of the
editor, so model/material hover cards stay consistent with current shader and
lighting behavior. The camera auto-orbits slowly to confirm the preview is
live. Model and material orbit is yaw-only so authored vertical axes remain
vertical. Model bounds are centred at the preview origin before yaw is
applied. The camera sits slightly above its fixed-radius orbit and pitches
toward the origin, avoiding a dead-on view without rotating the asset.
Textures stay on the bitmap path because they are regular Avalonia image
content; live model/material previews render into an external-image
`RhiTexture`, export the platform handle, and let Avalonia composite that
surface inside the hover popup. The hover renderer now keeps a dedicated
preview `IGameLoop` alive for the active card and re-renders that loaded scene
into the shared target each tick, instead of rebuilding a temporary world and
renderer per frame. Folder changes now cancel the active hover session before
the asset list is rebuilt, which prevents stale preview state from surviving
into a new content view.

`RoundedClipPanel` applies one rounded geometry to the bitmap fallback and the
composition child visual. The transparent top-level surface and the preview
clip therefore match the card geometry without square backing corners or a
GPU layer crossing the interior border.

This avoids the previous failure mode where the preview camera looked away from
the asset and the sky pass filled the icon instead, while also avoiding the
native-view occlusion problem from embedding another platform surface above the
editor UI.

## Render Graph Explorer

**Purpose**: Displays the active renderer pass sequence and live execution
telemetry without coupling Avalonia to the hot-reloaded renderer assembly.

### Public API Surface
- `RenderGraphExplorerViewModel`: Polls immutable diagnostics snapshots and
  prepares pass/resource rows for the editor.
- `RenderGraphExplorerView`: Displays frame totals, proportional pass timing
  bars, resource accesses, barriers, dependencies, memory sizes, lifetimes,
  alias groups, rolling frame history, and GPU work admission across dedicated
  Overview, Timeline, Passes, Resources, Budgets, and Shadows tabs. Shadow
  diagnostics include atlas memory, page residency, cache state, and stable
  page-slot identities for every punctual-light face.
- `IGameLoop.GetRenderGraphDiagnostics()`: Transfers the current snapshot
  across the assembly hot-reload boundary.

### Usage Example
Open the `Render Graph` bottom-panel tab while the viewport is rendering.
Expand a pass to inspect its declared resource accesses, upstream writers, and
incoming barriers. Filter by pass, resource, or alias-group name. Use `Pause`
to hold the current capture while retaining the last 60 sampled frames.
Live polling updates persistent pass rows in place, so expanded pass details do
not collapse between samples. Renderer plan versions invalidate stale rows and
history when switching between raster and path tracing.

The Shadows tab reads immutable allocator diagnostics. Moving a light changes
the face cache state to `transform queued` while page and slot labels remain
stable; a changed label indicates an actual allocation lifecycle event.
Each face row also reports its projected-importance score, target update
cadence, current tile resolution, and frames since its last atomic light
update.
Clicking a face opens the Shadow Atlas Inspector. Static and Movable select
the cached layer for that light face. The inspector blits the selected depth
tile through the main RHI device into one reused composition target; it creates
no swapchain, secondary device, scene renderer, or CPU bitmap.

### Performance Characteristics
The explorer refreshes at 4 Hz. Per-pass CPU timestamps are collected during
normal graph execution. Metal GPU timings use stage-boundary counter samples
on Apple silicon and draw/dispatch-boundary samples where supported elsewhere.
The required sample buffer is attached to every measured pass descriptor.
Results and the completed command-buffer validation span resolve
asynchronously through a triple-buffered timestamp pool; the render thread
never waits for profiling results. Unsupported backends keep GPU fields
explicitly pending.

The GPU frame readout uses the lower median of the latest 15 summed graphics
pass-marker workloads. Swapchain waits and fullscreen presentation pacing are
outside those marker scopes, so they cannot inflate the render-work readout or
throttle adaptive shadow updates.

The Transient Heap metric reports the physical alias heap allocated by the
render graph. GPU Committed reports live RHI allocation ownership. The
Resources tab shows graph-lifetime rows separately from committed heaps,
buffers, textures, model geometry, shadow pages, and editor preview targets.
Heap aliases remain visible without being counted twice. All metrics select
`B`, `KB`, `MB`, or `GB` based on magnitude.

The Budgets tab reports each GPU work domain's target milliseconds, learned
unit cost, adaptive unit cap, current-frame admitted/deferred units, and
cumulative totals. Cumulative values remain observable even when the 4 Hz
editor poll misses the specific frame that performed an update.

The editor bottom panel opens at 375 logical pixels, providing 25 percent more
vertical space for the content browser and diagnostics tools than the previous
300-pixel layout.

### Related Files
- `Game/IdPickingPass.cs`
- `Game/OutlineMaskPass.cs`
- `Game/OutlineCompositePass.cs`
- `Content/shaders/id_picking.slang`
- `Content/shaders/outline_mask.slang`
- `Content/shaders/outline_composite.slang`

## Material Layer Stacking & 3D Noise Parameters

The Material Editor supports multi-layer material stacking with procedural 3D noise and texture masking. The material pipeline evaluates:
- `noise_scale` (`NoiseScale` / `Layer2NoiseScale`): Spatial frequency scaling factor for 3D procedural noise mask evaluation (default `10.0`).
- `noise_threshold_min` (`NoiseThresholdMin` / `Layer2NoiseThresholdMin`): Lower threshold for noise mask smoothstep mapping (default `0.3`).
- `noise_threshold_max` (`NoiseThresholdMax` / `Layer2NoiseThresholdMax`): Upper threshold for noise mask smoothstep mapping (default `0.7`).

The GPU shaders (`pbr.slang` and `path_tracer.slang`) perform energy-conserving convex layer blending from bottom-to-top (`Layer 2` over `Base Material`, then `Layer 1` over `[Base Material + Layer 2]`).

## Numeric Input Validation & Transform NaN Guarding

To protect the selection outline pass and matrix pipeline from invalid inputs:
1. `NumericInputBehavior` filters keystrokes on numeric inputs across Avalonia views (`NumericUpDown` and `TextBox`), constraining characters to digits `0-9`, `-`, and `.`.
2. `InspectorViewModel` sanitizes transform positions, rotations, and scales against `NaN` and `Infinity`.
3. `OutlineMaskPass` and `SceneDataExtractor` validate model and view-projection matrices prior to GPU buffer upload, preventing selection outline corruption.
