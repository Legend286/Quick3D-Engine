// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class EcsWorld : IEntityStore, IDisposable
{
    private IntPtr _world;
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

    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EcsNative.EngineEcsShutdown(_world);
        _world = EcsNative.EngineEcsInit();
        _components.Clear();
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

/// <summary>
/// Generic mesh component holding vertex positions and colors for up to
/// <see cref="MaxVertices"/> vertices. Replaces the hard-coded
/// TriangleComponent with a flexible container that supports arbitrary
/// polygon counts (1..MaxVertices).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct MeshComponent
{
    public const int MaxVertices = 256;
    private const int MaxFloats = MaxVertices * 3;
    private const int PosBytes = MaxFloats * sizeof(float);
    private const int ColBytes = MaxFloats * sizeof(float);

    public int VertexCount;
    public fixed float Positions[MaxFloats];
    public fixed float Colors[MaxFloats];

    public static MeshComponent Create(float[] positions, float[] colors)
    {
        MeshComponent comp = default;
        int posLen = positions?.Length ?? 0;
        int colLen = colors?.Length ?? 0;
        int vertexCount = System.Math.Min(posLen / 3, MaxVertices);
        int colorCount = System.Math.Min(colLen / 3, MaxVertices);
        comp.VertexCount = System.Math.Min(vertexCount, colorCount);

        if (comp.VertexCount > 0)
        {
            int copyPosFloats = comp.VertexCount * 3;
            int copyPosBytes = copyPosFloats * sizeof(float);
            fixed (float* pSrc = positions)
            {
                System.Buffer.MemoryCopy(pSrc, comp.Positions, PosBytes, (ulong)copyPosBytes);
            }
            int copyColFloats = comp.VertexCount * 3;
            int copyColBytes = copyColFloats * sizeof(float);
            fixed (float* pSrc = colors)
            {
                System.Buffer.MemoryCopy(pSrc, comp.Colors, ColBytes, (ulong)copyColBytes);
            }
        }
        return comp;
    }

    public float[] GetPositions()
    {
        int count = System.Math.Max(VertexCount * 3, 0);
        float[] arr = new float[count];
        if (count > 0)
        {
            fixed (float* p = Positions)
            {
                Marshal.Copy((IntPtr)p, arr, 0, count);
            }
        }
        return arr;
    }

    public float[] GetColors()
    {
        int count = System.Math.Max(VertexCount * 3, 0);
        float[] arr = new float[count];
        if (count > 0)
        {
            fixed (float* p = Colors)
            {
                Marshal.Copy((IntPtr)p, arr, 0, count);
            }
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
