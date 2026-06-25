// SPDX-License-Identifier: MIT
// Managed wrapper for a Metal swapchain attached to a window handle.

using System;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiSwapchain : IDisposable
{
    private readonly RhiDevice _device;
    public IntPtr Handle { get; private set; }
    public uint Width
    {
        get
        {
            if (Handle == IntPtr.Zero) return 0;
            RhiNative.RhiSwapchainGetSize(Handle, out uint w, out _);
            return w;
        }
    }

    public uint Height
    {
        get
        {
            if (Handle == IntPtr.Zero) return 0;
            RhiNative.RhiSwapchainGetSize(Handle, out _, out uint h);
            return h;
        }
    }

    internal RhiSwapchain(RhiDevice device, IntPtr osWindow, uint w, uint h)
    {
        _device = device;
        int rc = RhiNative.RhiCreateSwapchain(device.Handle, osWindow, w, h, out IntPtr sc);
        if (rc != 0) throw new InvalidOperationException($"rhi_create_swapchain rc={rc}");
        Handle = sc;
    }

    /// <summary>Acquire the next drawable as an <see cref="RhiTexture"/>.
    /// Caller owns the texture and must Dispose it when the frame is done.
    /// Returns false if no drawable is currently available.</summary>
    public bool TryAcquireNextImage(out RhiTexture? image)
    {
        uint ok = RhiNative.RhiAcquireNextImage(Handle, out IntPtr tex);
        if (ok == 0) { image = null; return false; }
        image = new RhiTexture(tex, ownsHandle: true);
        return true;
    }

    public void Present() => RhiNative.RhiPresent(Handle);

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        // Zero the Handle field BEFORE invoking the native destroy so a
        // failed/partial C-side free doesn't get repeated by the finalizer.
        var h = Handle;
        Handle = IntPtr.Zero;
        RhiNative.RhiDestroySwapchain(h);
        GC.SuppressFinalize(this);
    }

    /// <summary>Safety net: see <see cref="RhiBuffer"/>.</summary>
    ~RhiSwapchain() => Dispose();
}
