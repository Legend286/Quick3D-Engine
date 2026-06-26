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
public struct TriangleComponent
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
    public float[] Positions;  // length 3*3
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 9)]
    public float[] Colors;     // length 3*3
}

[StructLayout(LayoutKind.Sequential)]
public struct TransformComponent
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] Translate;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] Rotate;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
    public float[] Scale;
}
