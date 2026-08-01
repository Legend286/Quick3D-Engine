// SPDX-License-Identifier: MIT
// Managed buffer wrapper.

using System;
using System.Runtime.InteropServices;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiBuffer : IDisposable
{
    public IntPtr Handle { get; private set; }
    public ulong Size { get; }

    /// <summary>Gets whether the native buffer has been released.</summary>
    public bool IsDisposed => Handle == IntPtr.Zero;

    /// <summary>Gets the GPU virtual address of a live buffer.</summary>
    /// <exception cref="ObjectDisposedException">
    /// Thrown when the buffer has already been released.
    /// </exception>
    public ulong DeviceAddress
    {
        get
        {
            IntPtr handle = GetLiveHandle();
            return RhiNative.RhiGetBufferDeviceAddress(handle);
        }
    }
    private readonly bool _owns;
    private readonly long _allocationId;

    internal RhiBuffer(
        IntPtr handle,
        ulong size,
        bool ownsHandle = true,
        bool trackAllocation = true)
    {
        Handle = handle;
        Size = size;
        _owns = ownsHandle;
        if (ownsHandle && trackAllocation)
        {
            _allocationId = GpuResourceRegistry.Register(
                $"Buffer 0x{handle.ToInt64():X}",
                "Buffer",
                "Buffer",
                size);
        }
    }

    public static RhiBuffer Create(RhiDevice device, ulong size, RhiNative.BufferUsage usage)
    {
        var desc = new RhiNative.BufferDesc
        {
            Abi = 1,
            Size = size,
            Usage = usage,
        };
        int rc = RhiNative.RhiCreateBuffer(device.Handle, in desc, out IntPtr buf);
        if (rc != 0 || buf == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"rhi_create_buffer rc={rc} handle=0x{buf.ToInt64():X}");
        }
        return new RhiBuffer(buf, size);
    }

    public void Upload<T>(ReadOnlySpan<T> data) where T : unmanaged
    {
        if (data.Length == 0) return;
        unsafe
        {
            fixed (T* p = data)
            {
                int rc = RhiNative.RhiBufferUpload(
                    GetLiveHandle(),
                    (IntPtr)p,
                    (ulong)(data.Length * sizeof(T)));
                if (rc != 0) throw new InvalidOperationException($"rhi_buffer_upload rc={rc}");
            }
        }
    }

    public void Upload(IntPtr data, ulong sizeBytes)
    {
        if (sizeBytes == 0) return;
        int rc = RhiNative.RhiBufferUpload(
            GetLiveHandle(),
            data,
            sizeBytes);
        if (rc != 0) throw new InvalidOperationException($"rhi_buffer_upload rc={rc}");
    }

    /// <summary>
    /// Reads a synchronous byte range from a GPU buffer.
    /// </summary>
    public byte[] Readback(ulong offsetBytes, ulong sizeBytes)
    {
        if (sizeBytes == 0) return Array.Empty<byte>();
        if (sizeBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(sizeBytes));

        byte[] result = new byte[(int)sizeBytes];
        unsafe
        {
            fixed (byte* p = result)
            {
                int rc = RhiNative.RhiBufferReadback(
                    GetLiveHandle(),
                    offsetBytes,
                    (IntPtr)p,
                    sizeBytes);
                if (rc != 0)
                    throw new InvalidOperationException(
                        $"rhi_buffer_readback rc={rc}");
            }
        }
        return result;
    }

    /// <summary>
    /// Assigns an allocation label and category shown by GPU diagnostics.
    /// </summary>
    public void SetDebugName(string name, string category = "Buffer")
        => GpuResourceRegistry.Rename(
            _allocationId,
            name,
            category);

    public void Dispose()
    {
        if (Handle == IntPtr.Zero || !_owns) return;
        // Zero the Handle field BEFORE invoking the native destroy so a
        // failed/partial C-side free doesn't get repeated by the finalizer.
        var h = Handle;
        Handle = IntPtr.Zero;
        GpuResourceRegistry.Unregister(_allocationId);
        RhiNative.RhiDestroyBuffer(h);
        GC.SuppressFinalize(this);
    }

    /// <summary>Safety net: if the caller forgets Dispose(), the finalizer
    /// thread still drops the native MTLBuffer before GC reclaims. Targets
    /// the common LLM mistake of creating Rhi* without pairing dispose.</summary>
    ~RhiBuffer() => Dispose();

    private IntPtr GetLiveHandle()
    {
        IntPtr handle = Handle;
        if (handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(RhiBuffer));
        return handle;
    }
}
