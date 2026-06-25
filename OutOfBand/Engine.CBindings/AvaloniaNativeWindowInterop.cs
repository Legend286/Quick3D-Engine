// SPDX-License-Identifier: MIT
// Avalonia 11 -> native window-handle recovery for the Metal RHI.
//
// Uses Avalonia's PUBLIC platform-handle API. On non-macOS we surface a
// PlatformNotSupportedException so the editor logs a clear error in the
// console panel rather than silently passing an HWND into a Metal path.

using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Engine.CBindings;

public static class AvaloniaNativeWindowInterop
{
    /// <summary>Returns the NSWindow* handle backing the Avalonia Window on
    /// macOS. Throws PlatformNotSupportedException on other platforms -
    /// the Metal RHI path is macOS-only. Returns IntPtr.Zero if window
    /// is null (TopLevel-not-yet-attached case).</summary>
    public static IntPtr GetMacOsWindowPointer(Window? window)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException(
                "AvaloniaNativeWindowInterop.GetMacOsWindowPointer is macOS-only. " +
                "Vulkan swapchain support for Windows/Linux is Phase 4+.");

        if (window is null) return IntPtr.Zero;

        var top = TopLevel.GetTopLevel(window);
        if (top is null) return IntPtr.Zero;
        if (top.TryGetPlatformHandle() is { } h)
            return h.Handle;
        return IntPtr.Zero;
    }

    /// <summary>Read the rendered frame size from the Avalonia Window +
    /// DPI scale. Returns (Width, Height) in physical pixels suitable for
    /// CAMetalLayer.drawableSize.</summary>
    public static (uint Width, uint Height) ReadFrameMetrics(Window? window)
    {
        if (window is null) return (1, 1);
        double scale = 1.0;
        // Avalonia 11 moved the window-scale factor from IPlatformHandle
        // (gone in 11.x) onto TopLevel.RenderScaling. Read it from the
        // resolved TopLevel so the readback bitmap gets filled at the
        // right pixel density.
        var top = TopLevel.GetTopLevel(window);
        if (top is not null && top.RenderScaling > 0)
            scale = top.RenderScaling;
        double w = window.Width  * scale;
        double h = window.Height * scale;
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        return ((uint)w, (uint)h);
    }

    /// <summary>Allocate a child NSView hosting a CAMetalLayer-compatible
    /// hierarchy under <paramref name="parent"/>. The view is autoreleased-
    /// pool-managed; caller owns one strong reference until
    /// <see cref="DestroyMacosMetalView"/> is invoked.</summary>
    public static IntPtr CreateMacosMetalView(IntPtr parent, uint width, uint height)
        => RhiNative.RhiCreateMacosMetalView(parent, width, height);

    /// <summary>Release the NSView returned from
    /// <see cref="CreateMacosMetalView"/>. Removes the child from its
    /// superview and drops ARC's strong reference; safe to call from any
    /// thread (the @autoreleasepool scope inside the .mm side strictly
    /// contains the AppKit message dispatch).</summary>
    public static void DestroyMacosMetalView(IntPtr view)
        => RhiNative.RhiDestroyMacosMetalView(view);
}
