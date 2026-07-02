// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Numerics;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class EcsWorld : IEntityStore, IDisposable
{
    private IntPtr _world;
    private readonly ConcurrentDictionary<Type, ulong> _components = new();
    private readonly System.Collections.Generic.List<ulong> _entities = new();
    private bool _disposed;

    public IntPtr NativeWorld => _world;
    public System.Collections.Generic.IReadOnlyList<ulong> Entities => _entities;
    public event Action<ulong>? OnEntityCreated;
    public event Action? OnWorldCleared;

    public EcsWorld()
    {
        _world = EcsNative.EngineEcsInit();
        if (_world == IntPtr.Zero)
        {
            throw new InvalidOperationException("Failed to initialize FLECS world");
        }
    }

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EcsNative.EngineEcsShutdown(_world);
        _world = EcsNative.EngineEcsInit();
        _components.Clear();
        _entities.Clear();
        OnWorldCleared?.Invoke();
    }

    public ulong CreateEntity()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ulong ent = EcsNative.EngineEcsCreateEntity(_world);
        _entities.Add(ent);
        OnEntityCreated?.Invoke(ent);
        return ent;
    }

    private ulong GetOrRegisterComponent<T>() where T : unmanaged
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

    private static int GetAlignment<T>() where T : unmanaged
    {
        return 8;
    }

    public unsafe void Set<T>(ulong entity, in T component) where T : unmanaged
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ulong cid = GetOrRegisterComponent<T>();
        int size = Marshal.SizeOf<T>();
        fixed (T* ptr = &component)
        {
            EcsNative.EngineEcsSetComponent(_world, entity, cid, ptr, (nuint)size);
        }
    }

    public unsafe bool TryGet<T>(ulong entity, out T component) where T : unmanaged
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

/// <summary>
/// Generic mesh component holding vertex positions and colors for up to
/// <see cref="MaxVertices"/> vertices. Replaces the hard-coded
[StructLayout(LayoutKind.Sequential)]
public struct ModelComponent
{
    public ulong ModelId;

    public static ModelComponent Create(ulong modelId)
    {
        return new ModelComponent { ModelId = modelId };
    }
}

[StructLayout(LayoutKind.Sequential)]
public struct MaterialComponent
{
    public ulong MaterialId;
}

[StructLayout(LayoutKind.Sequential)]
public struct DirectionalLightComponent
{
    public Vector3 Color;
    public float Intensity;
    public Vector3 Direction;
    public bool CastShadows;
}


