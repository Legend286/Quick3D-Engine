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
   model bounds so the full asset stays inside the icon frame.
2. Material thumbnails render a preview sphere with the same no-sky light rig
   and a closer negative-Z camera placement so the sphere fills the icon.
3. Texture thumbnails bypass scene lighting entirely and use a dedicated blit
   pass that samples the texture directly into the icon target.
4. The editor preserves already loaded thumbnail bitmaps across asset-list
   refreshes and reuses on-disk cache files immediately, so icons no longer
   disappear while unrelated thumbnails are being generated.
5. Thumbnail generation now runs through a single preview worker. The asset
   loaders and registry still use shared global caches, so serializing icon
   renders avoids GPU and asset-cache races when browsing heavy model folders.

## Asset Hover Preview

The content browser now exposes a delayed hover-preview card for renderable
assets. Textures show an enlarged cached image preview inside the main editor
window. Models and materials switch to a live top-level preview window that
imports a shared GPU render target into Avalonia composition, instead of
scaling up a thumbnail bitmap or spinning up a second viewport host.

The live preview surface uses the same engine renderer path as the rest of the
editor, so model/material hover cards stay consistent with current shader and
lighting behavior. The camera auto-orbits slowly to confirm the preview is
live. Textures stay on the bitmap path because they are regular Avalonia image
content; live model/material previews render into an external-image
`RhiTexture`, export the platform handle, and let Avalonia composite that
surface inside the hover popup. The hover renderer now keeps a dedicated
preview `IGameLoop` alive for the active card and re-renders that loaded scene
into the shared target each tick, instead of rebuilding a temporary world and
renderer per frame. Folder changes now cancel the active hover session before
the asset list is rebuilt, which prevents stale preview state from surviving
into a new content view.

This avoids the previous failure mode where the preview camera looked away from
the asset and the sky pass filled the icon instead, while also avoiding the
native-view occlusion problem from embedding another platform surface above the
editor UI.

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
