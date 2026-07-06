using System;
using System.Runtime.InteropServices;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiAccelStruct : IDisposable
{
    public IntPtr Handle { get; private set; }

    internal RhiAccelStruct(IntPtr handle)
    {
        Handle = handle;
    }

    public static unsafe RhiAccelStruct CreateBlas(RhiDevice device, ReadOnlySpan<RhiNative.BlasGeometryDesc> geometries)
    {
        fixed (RhiNative.BlasGeometryDesc* geoPtr = geometries)
        {
            var desc = new RhiNative.AccelStructDesc
            {
                Abi = 6,
                Type = RhiNative.AccelStructType.Blas,
                Geometries = (IntPtr)geoPtr,
                GeometryCount = (uint)geometries.Length,
                Instances = IntPtr.Zero,
                InstanceCount = 0
            };

            int rc = RhiNative.RhiCreateAccelStruct(device.Handle, in desc, out IntPtr handle);
            if (rc != 0)
            {
                throw new InvalidOperationException($"rhi_create_accel_struct returned {rc}");
            }
            return new RhiAccelStruct(handle);
        }
    }

    public static unsafe RhiAccelStruct CreateTlas(RhiDevice device, ReadOnlySpan<RhiNative.TlasInstanceDesc> instances)
    {
        fixed (RhiNative.TlasInstanceDesc* instPtr = instances)
        {
            var desc = new RhiNative.AccelStructDesc
            {
                Abi = 6,
                Type = RhiNative.AccelStructType.Tlas,
                Geometries = IntPtr.Zero,
                GeometryCount = 0,
                Instances = (IntPtr)instPtr,
                InstanceCount = (uint)instances.Length
            };

            int rc = RhiNative.RhiCreateAccelStruct(device.Handle, in desc, out IntPtr handle);
            if (rc != 0)
            {
                throw new InvalidOperationException($"rhi_create_accel_struct returned {rc}");
            }
            return new RhiAccelStruct(handle);
        }
    }

    public static RhiAccelStruct Create(RhiDevice device, in RhiNative.AccelStructDesc desc)
    {
        int rc = RhiNative.RhiCreateAccelStruct(device.Handle, in desc, out IntPtr handle);
        if (rc != 0)
        {
            throw new InvalidOperationException($"rhi_create_accel_struct returned {rc}");
        }
        return new RhiAccelStruct(handle);
    }

    ~RhiAccelStruct()
    {
        Dispose(false);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (Handle != IntPtr.Zero)
        {
            RhiNative.RhiDestroyAccelStruct(Handle);
            Handle = IntPtr.Zero;
        }
    }
}
