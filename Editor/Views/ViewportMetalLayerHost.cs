// SPDX-License-Identifier: MIT
using System;
using Avalonia.Controls;
using Avalonia.Platform;
using Engine.CBindings;

namespace Engine.Editor.Views;

/// <summary>
/// Avalonia 11.2.7 <see cref="NativeControlHost"/> subclass that owns a
/// child NSView returned by <c>rhi_create_macos_metal_view</c>. The child
/// NSView is the host into which the C RHI attaches a <c>CAMetalLayer</c>
/// sublayer on the next call to <c>rhi_create_swapchain</c>. Exposing the
/// raw IntPtr handle keeps the view-model free of Objective-C runtime
/// knowledge: it just hands the handle down into the C layer.
///
/// Resize handling: the host's <see cref="Control.BoundsProperty"/>
/// propagates Avalonia-driven layout changes. The view-model subscribes to
/// <see cref="Control.SizeChanged"/> on this control and rebuilds the
/// swapchain with the new dimensions in response.
///
/// Override naming per Avalonia 11.2.7's NativeControlHost source:
/// <c>CreateNativeControlCore(IPlatformHandle)</c> returns a non-nullable
/// <see cref="IPlatformHandle"/> (we fall back to
/// <c>base.CreateNativeControlCore</c> on non-macOS or init failure so the
/// default NSView is still provided). The cleanup hook is
/// <c>DestroyNativeControlCore</c> - the base class's private
/// <c>DestroyNativeControl</c> method is non-virtual and unreachable.
/// </summary>
public sealed class ViewportMetalLayerHost : NativeControlHost
{
    /// <summary>Default initial size before Avalonia lays the panel out.
    /// The view-model will trigger a swapchain rebuild once the actual
    /// Bounds settle, so any mismatch from this default is corrected
    /// before the first frame is rendered.</summary>
    public const uint DefaultInitialWidth = 1280;
    public const uint DefaultInitialHeight = 720;

    private IntPtr _activeHandle;

    /// <summary>Pointer to the inner NSView hosting the CAMetalLayer. Returns
    /// <see cref="IntPtr.Zero"/> until <see cref="CreateNativeControlCore"/>
    /// has executed. Safe to read on the UI thread.</summary>
    public IntPtr NativeViewHandle =>
        _activeHandle != IntPtr.Zero ? _activeHandle : IntPtr.Zero;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        // non-macOS or init failure -> hand the base class the default child
        // view the platform impl normally provides.
        if (!OperatingSystem.IsMacOS() || parent.Handle == IntPtr.Zero)
            return base.CreateNativeControlCore(parent);

        IntPtr view = AvaloniaNativeWindowInterop.CreateMacosMetalView(
            parent.Handle, DefaultInitialWidth, DefaultInitialHeight);
        if (view == IntPtr.Zero)
            return base.CreateNativeControlCore(parent);

        _activeHandle = view;
        return new PlatformHandle(view, "NSView");
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (control.Handle != IntPtr.Zero)
            AvaloniaNativeWindowInterop.DestroyMacosMetalView(control.Handle);
        _activeHandle = IntPtr.Zero;
        // base.DestroyNativeControlCore calls INativeControlHostDestroyableControlHandle.Destroy
        // for handles that implement it; PlatformHandle(view, "NSView") does not, so base
        // would be a no-op here. We skip it to make "we own the lifecycle" intent explicit.
    }
}
