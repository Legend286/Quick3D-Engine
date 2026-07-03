// SPDX-License-Identifier: MIT
// Managed wrapper around the bindless texture heap exposed by the C RHI.
//
// The heap owns an `id<MTLArgumentEncoder>` (on Metal) backed by a single
// MTLBuffer whose encoded layout holds an array of texture handles. Maps a
// stable slot id (uint) to/from a GPU-resident texture, surviving reload
// and per-frame pipeline churn. Coexists with the legacy
// `rhi_cmd_bind_texture_array` path — call sites can migrate piecemeal.
//
// Lifetime: registry (RHI) owns the heap. C# keeps an IntPtr; Dispose
// forwards to rhi_destroy_bindless_heap and zeroes the handle before
// returning so the finalizer is a no-op.

using System;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiBindlessHeap : IDisposable
{
    public IntPtr Handle { get; private set; }
    public uint Capacity { get; }
    public bool IsInitialized => Handle != IntPtr.Zero;

    private readonly bool _owns;

    /// <summary>
    /// Construct a bindless heap. <paramref name="capacity"/> = 0 requests the
    /// device's natural cap (Metal: MTLDevice.maxArgumentBufferSamplerCount,
    /// falling back to 65536 on legacy OS).
    /// </summary>
    public RhiBindlessHeap(RhiDevice device, uint capacity = 0)
    {
        var desc = new RhiNative.BindlessHeapDesc { Abi = 1, Capacity = capacity };
        int rc = RhiNative.RhiCreateBindlessHeap(device.Handle, in desc, out IntPtr heap);
        if (rc != 0) throw new InvalidOperationException($"rhi_create_bindless_heap rc={rc}");
        Handle = heap;
        Capacity = capacity; // back-end resolved the actual capacity; the C ABI has no getter yet
        _owns = true;
    }

    internal RhiBindlessHeap(IntPtr handle, bool ownsHandle)
    {
        Handle = handle;
        _owns = ownsHandle;
    }

    public uint Register(RhiTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        int rc = RhiNative.RhiBindlessRegisterTexture(Handle, texture.Handle, out uint slot);
        if (rc != 0) throw new InvalidOperationException($"rhi_bindless_register_texture rc={rc}");
        return slot;
    }

    public void Release(uint slot) =>
        RhiNative.RhiBindlessReleaseTexture(Handle, slot);

    public bool TryLookup(RhiTexture texture, out uint slot)
    {
        ArgumentNullException.ThrowIfNull(texture);
        int rc = RhiNative.RhiBindlessLookupSlot(Handle, texture.Handle, out slot);
        return rc == 0;
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero || !_owns) return;
        var h = Handle;
        Handle = IntPtr.Zero;
        RhiNative.RhiDestroyBindlessHeap(h);
        GC.SuppressFinalize(this);
    }

    ~RhiBindlessHeap() => Dispose();
}
