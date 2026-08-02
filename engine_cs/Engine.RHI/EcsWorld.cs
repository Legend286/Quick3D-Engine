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
    public event Action<ulong>? OnEntityDeleted;
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

    public ulong RestoreEntity(ulong entity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (entity == 0 || _entities.Contains(entity))
            return 0;

        ulong restored =
            EcsNative.EngineEcsRestoreEntity(_world, entity);
        if (restored == 0)
            return 0;

        _entities.Add(restored);
        OnEntityCreated?.Invoke(restored);
        return restored;
    }

    public bool DeleteEntity(ulong entity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entities.Remove(entity))
            return false;

        EcsNative.EngineEcsDeleteEntity(_world, entity);
        OnEntityDeleted?.Invoke(entity);
        return true;
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
    public bool StaticShadowCaster;

    public static ModelComponent Create(
        ulong modelId,
        bool staticShadowCaster = true)
    {
        return new ModelComponent
        {
            ModelId = modelId,
            StaticShadowCaster = staticShadowCaster,
        };
    }
}

/// <summary>CPU-owned animation intent consumed by the GPU animation pass.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AnimatorComponent
{
    /// <summary>Bit set when this animator should advance and evaluate.</summary>
    public const uint ActiveFlag = 1u << 0;

    /// <summary>Stable skeleton asset ID registered by the animation asset service.</summary>
    public uint SkeletonId;

    /// <summary>Stable animation clip asset ID registered by the animation asset service.</summary>
    public uint BaseClipId;

    /// <summary>Current clip time in seconds.</summary>
    public float Time;

    /// <summary>Playback rate; zero pauses without changing active state.</summary>
    public float PlaybackRate;

    /// <summary>Flags controlling GPU evaluation.</summary>
    public uint Flags;

    /// <summary>Generation used to reject stale slot work after entity reuse.</summary>
    public uint Generation;

    /// <summary>Creates an active base-clip animator.</summary>
    public static AnimatorComponent Create(
        uint skeletonId,
        uint clipId,
        float playbackRate = 1.0f,
        bool looping = true)
        => new()
        {
            SkeletonId = skeletonId,
            BaseClipId = clipId,
            PlaybackRate = playbackRate,
            Flags = ActiveFlag | (looping ? 1u << 1 : 0u),
            Generation = 1,
        };
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
    public float AngularRadius;
    public bool CastShadows;
}

[StructLayout(LayoutKind.Sequential)]
public struct PointLightComponent
{
    public Vector3 Color;
    public float Intensity;
    public float Range;
    public float SourceRadius;
    public bool CastShadows;
}

[StructLayout(LayoutKind.Sequential)]
public struct SpotLightComponent
{
    public Vector3 Color;
    public float Intensity;
    public float Range;
    public Vector3 Direction;
    public float InnerCone;
    public float OuterCone;
    public float SourceRadius;
    public bool CastShadows;
}

/// <summary>
/// Drives a light around an elliptical orbit for scene-authored animation.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct OrbitingLightComponent
{
    public Vector3 Center;
    public float Radius;
    public float AngularSpeed;
    public float Phase;
    public float VerticalAmplitude;
    public float VerticalFrequency;
    public float OrbitHeight;
    public bool AimAtCenter;
}

/// <summary>Drives a dynamic procedural model along one local axis.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct OscillatingModelComponent
{
    public Vector3 Origin;
    public Vector3 Axis;
    public float Amplitude;
    public float Frequency;
    public float Phase;
}

/// <summary>
/// Marks runtime-generated entities that remain represented by scene metadata.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct ProceduralDemoEntityComponent
{
    public byte Value;
}
