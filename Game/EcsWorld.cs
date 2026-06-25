// SPDX-License-Identifier: MIT
// Tiny in-process ECS world for the Phase 2 hello-triangle path.
//
// Phase 3 will swap this with a Flecs.NET binding behind the same IEntityStore
// surface. The hello-triangle does not need FLECS breadth - one entity, two
// components, one transform.

using System;
using System.Collections.Generic;

namespace Engine.Game;

public interface IEntityStore
{
    public uint CreateEntity();
    public void Set<T>(uint entity, in T component) where T : struct;
    public bool TryGet<T>(uint entity, out T component) where T : struct;
}

public sealed class EcsWorld : IEntityStore, IDisposable
{
    private readonly Dictionary<uint, Dictionary<System.Type, object>> _components = new();
    private uint _nextId = 1;
    private bool _disposed;

    public uint CreateEntity() => _nextId++;

    public void Set<T>(uint entity, in T component) where T : struct
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EcsWorld));
        if (!_components.TryGetValue(entity, out var dict))
        {
            dict = new();
            _components[entity] = dict;
        }
        dict[typeof(T)] = component;
    }

    public bool TryGet<T>(uint entity, out T component) where T : struct
    {
        component = default;
        if (_disposed) return false;
        if (!_components.TryGetValue(entity, out var dict)) return false;
        if (!dict.TryGetValue(typeof(T), out var boxed)) return false;
        component = (T)boxed;
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _components.Clear();
        GC.SuppressFinalize(this);
    }

    /// <summary>Read-only access to the entity-id range used for Phase-2
    /// sparse queries. Phase 3's Flecs.NET replaces this with proper iterators.</summary>
    public IEnumerable<uint> EntityIds => _components.Keys;
}

public struct TriangleComponent
{
    public float[] Positions;  // length 3*3
    public float[] Colors;     // length 3*3
}

public struct TransformComponent
{
    public float[] Translate;
    public float[] Rotate;
    public float[] Scale;
}
