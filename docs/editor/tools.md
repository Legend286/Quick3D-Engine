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
