// SPDX-License-Identifier: MIT
using System;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiFence : IDisposable
{
    public readonly struct ExternalSemaphoreHandle
    {
        public ExternalSemaphoreHandle(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
        public bool IsValid => Handle != IntPtr.Zero;
    }

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

    public ExternalSemaphoreHandle ExportExternalHandle()
    {
        int rc = RhiNative.RhiFenceExportExternalHandle(Handle, out IntPtr handle);
        if (rc != 0) throw new InvalidOperationException($"rhi_fence_export_external_handle failed: {rc}");
        return new ExternalSemaphoreHandle(handle);
    }

    public static void ReleaseExternalSemaphoreHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        RhiNative.RhiReleaseExternalSemaphoreHandle(handle);
    }

    ~RhiFence() => Dispose();
}
