// SPDX-License-Identifier: MIT
// Managed wrapper for a Metal swapchain attached to a window handle.

using System;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiSwapchain : IDisposable
{
    private readonly RhiDevice _device;
    public IntPtr Handle { get; }
    public uint Width  { get; }
    public uint Height { get; }

    internal RhiSwapchain(RhiDevice device, IntPtr osWindow, uint w, uint h)
    {
        _device = device;
        int rc = RhiNative.RhiCreateSwapchain(device.Handle, osWindow, w, h, out IntPtr sc);
        if (rc != 0) throw new InvalidOperationException($"rhi_create_swapchain rc={rc}");
        Handle  = sc;
        Width   = w;
        Height  = h;
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
        RhiNative.RhiDestroySwapchain(Handle);
        GC.SuppressFinalize(this);
    }
}
