// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class EcsWorld : IEntityStore, IDisposable
{
    private readonly IntPtr _world;
    private readonly ConcurrentDictionary<Type, ulong> _components = new();
    private bool _disposed;

    public IntPtr NativeWorld => _world;

    public EcsWorld()
    {
        _world = EcsNative.EngineEcsInit();
        if (_world == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to initialize FLECS world");
        }
    }

    public ulong CreateEntity()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return EcsNative.EngineEcsCreateEntity(_world);
    }

    private ulong GetOrRegisterComponent<T>() where T : struct
    {
        return _components.GetOrAdd(typeof(T), type =>
        {
            string name = type.FullName ?? type.Name;
            int size = Marshal.SizeOf<T>();
            int alignment = GetAlignment<T>();
            ulong cid = EcsNative.EngineEcsRegisterComponent(_world, name, (nuint)size, (nuint)alignment);
            if (cid == 0)
            {
                throw new InvalidOperationException($"Failed to register FLECS component: {name}");
            }
            return cid;
        });
    }

    private static int GetAlignment<T>() where T : struct
    {
        return 8;
    }

    public unsafe void Set<T>(ulong entity, in T component) where T : struct
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ulong cid = GetOrRegisterComponent<T>();
        int size = Marshal.SizeOf<T>();
        fixed (T* ptr = &component)
        {
            EcsNative.EngineEcsSetComponent(_world, entity, cid, ptr, (nuint)size);
        }
    }

    public unsafe bool TryGet<T>(ulong entity, out T component) where T : struct
    {
        component = default;
        if (_disposed) return false;
        ulong cid = GetOrRegisterComponent<T>();
        int size = Marshal.SizeOf<T>();
        fixed (T* ptr = &component)
        {
            int rc = EcsNative.EngineEcsGetComponent(_world, entity, cid, ptr, (nuint)size);
            return rc != 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        EcsNative.EngineEcsShutdown(_world);
        GC.SuppressFinalize(this);
    }

    ~EcsWorld() => Dispose();
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TriangleComponent
{
    public fixed float Positions[9];
    public fixed float Colors[9];

    public static TriangleComponent Create(float[] positions, float[] colors)
    {
        TriangleComponent comp = default;
        if (positions != null && positions.Length >= 9)
        {
            fixed (float* pSrc = positions)
            {
                System.Buffer.MemoryCopy(pSrc, comp.Positions, 36, 36);
            }
        }
        if (colors != null && colors.Length >= 9)
        {
            fixed (float* pSrc = colors)
            {
                System.Buffer.MemoryCopy(pSrc, comp.Colors, 36, 36);
            }
        }
        return comp;
    }

    public float[] GetPositions()
    {
        float[] arr = new float[9];
        fixed (float* p = Positions)
        {
            Marshal.Copy((IntPtr)p, arr, 0, 9);
        }
        return arr;
    }

    public float[] GetColors()
    {
        float[] arr = new float[9];
        fixed (float* p = Colors)
        {
            Marshal.Copy((IntPtr)p, arr, 0, 9);
        }
        return arr;
    }
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct TransformComponent
{
    public fixed float Translate[3];
    public fixed float Rotate[3];
    public fixed float Scale[3];

    public static TransformComponent Create(float[] translate, float[] rotate, float[] scale)
    {
        TransformComponent comp = default;
        if (translate != null && translate.Length >= 3)
        {
            fixed (float* pSrc = translate)
            {
                System.Buffer.MemoryCopy(pSrc, comp.Translate, 12, 12);
            }
        }
        if (rotate != null && rotate.Length >= 3)
        {
            fixed (float* pSrc = rotate)
            {
                System.Buffer.MemoryCopy(pSrc, comp.Rotate, 12, 12);
            }
        }
        if (scale != null && scale.Length >= 3)
        {
            fixed (float* pSrc = scale)
            {
                System.Buffer.MemoryCopy(pSrc, comp.Scale, 12, 12);
            }
        }
        return comp;
    }

    public float[] GetTranslate()
    {
        float[] arr = new float[3];
        fixed (float* p = Translate) Marshal.Copy((IntPtr)p, arr, 0, 3);
        return arr;
    }

    public float[] GetRotate()
    {
        float[] arr = new float[3];
        fixed (float* p = Rotate) Marshal.Copy((IntPtr)p, arr, 0, 3);
        return arr;
    }

    public float[] GetScale()
    {
        float[] arr = new float[3];
        fixed (float* p = Scale) Marshal.Copy((IntPtr)p, arr, 0, 3);
        return arr;
    }
}
