// SPDX-License-Identifier: MIT
// Managed wrapper for the EngineC RHI device lifecycle.

using System;
using Engine.CBindings;

namespace Engine.RHI;

/// <summary>Single-owner handle to a Metal device + command queue. Disposing
/// disposes the underlying device but is intentionally LAST - destroy swapchains,
/// command-list resources, and pipeline state in user code first.</summary>
public sealed class RhiDevice : IDisposable
{
    public IntPtr Handle { get; private set; }

    public bool IsInitialized => Handle != IntPtr.Zero;

    public RhiDevice()
    {
        int rc = RhiNative.RhiInit(out IntPtr dev);
        if (rc != 0) throw new InvalidOperationException($"rhi_init failed rc={rc}");
        Handle = dev;
    }

    public RhiSwapchain CreateSwapchain(IntPtr osWindow, uint width, uint height)
        => new(this, osWindow, width, height);

    public void Dispose()
    {
        if (!IsInitialized) return;
        RhiNative.RhiShutdown(Handle);
        Handle = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    ~RhiDevice()
    {
        if (IsInitialized)
        {
            // last-resort; caller must dispose explicitly.
            RhiNative.RhiShutdown(Handle);
        }
    }
}
