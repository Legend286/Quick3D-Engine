// SPDX-License-Identifier: MIT
using System;
using Engine.CBindings;

namespace Engine.RHI;

public class RhiSampler : IDisposable
{
    public IntPtr Handle { get; private set; }

    internal RhiSampler(IntPtr handle)
    {
        Handle = handle;
    }

    public static RhiSampler Create(RhiDevice device)
    {
        IntPtr handle = RhiNative.RhiCreateSampler(device.Handle);
        if (handle == IntPtr.Zero)
            throw new Exception("Failed to create RHI sampler");
        return new RhiSampler(handle);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            RhiNative.RhiDestroySampler(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
