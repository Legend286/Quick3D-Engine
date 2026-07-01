// SPDX-License-Identifier: MIT
using System;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiFence : IDisposable
{
    public IntPtr Handle { get; private set; }

    public RhiFence(RhiDevice device)
    {
        int rc = RhiNative.RhiCreateFence(device.Handle, out IntPtr h);
        if (rc != 0) throw new InvalidOperationException($"rhi_create_fence failed: {rc}");
        Handle = h;
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            var h = Handle;
            Handle = IntPtr.Zero;
            RhiNative.RhiDestroyFence(h);
            GC.SuppressFinalize(this);
        }
    }

    ~RhiFence() => Dispose();
}
