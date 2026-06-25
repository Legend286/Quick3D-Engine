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
(rebuilt on `SizeChanged`), and the sublayer's
`autoresizingMask` keeps it glued to the host's bounds when
Avalonia resizes. Passes encode against the next-drawable
texture acquired from the swapchain; submit and the new
drawable composites into the host's layer tree at the next
vsync.

There is no `WriteableBitmap` + `Image` readback path
anymore on macOS - Metal draws straight to the embedded
NSView, so the Avl Skia round-trip is bypassed entirely.
`Engine.RHI/RhiTexture.Readback` is preserved for editor
preview screenshots (Phase 3+).
