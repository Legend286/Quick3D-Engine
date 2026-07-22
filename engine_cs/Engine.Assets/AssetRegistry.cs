// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using Engine.RHI;

namespace Engine.Assets;

public sealed class AssetTextureEntry
{
    public ulong Id { get; init; }
    public RhiTexture Texture { get; init; } = null!;
    public uint HeapSlot { get; set; }
    public int RefCount { get; set; }
}

public static class AssetRegistry
{
    private static readonly object _lock = new();

    private static readonly Dictionary<ulong, Mesh> _meshes = new();
    private static ulong _nextMeshId = 1;

    public static ulong RegisterMesh(Mesh mesh)
    {
        lock (_lock)
        {
            ulong id = _nextMeshId++;
            _meshes[id] = mesh;
            return id;
        }
    }

    public static Mesh? GetMesh(ulong id)
    {
        lock (_lock)
        {
            if (_meshes.TryGetValue(id, out Mesh? mesh))
                return mesh;
            return null;
        }
    }

    private static readonly Dictionary<ulong, Material> _materials = new();
    private static ulong _nextMaterialId = 1;

    public static ulong RegisterMaterial(Material mat)
    {
        lock (_lock)
        {
            ulong id = _nextMaterialId++;
            _materials[id] = mat;
            return id;
        }
    }

    public static Material? GetMaterial(ulong id)
    {
        lock (_lock)
        {
            if (_materials.TryGetValue(id, out Material? mat))
                return mat;
            return null;
        }
    }

    private static readonly Dictionary<ulong, Model> _models = new();
    private static ulong _nextModelId = 1;

    public static ulong RegisterModel(Model model)
    {
        lock (_lock)
        {
            ulong id = _nextModelId++;
            _models[id] = model;
            return id;
        }
    }

    public static Model? GetModel(ulong id)
    {
        lock (_lock)
        {
            if (_models.TryGetValue(id, out Model? model))
                return model;
            return null;
        }
    }

    // Texture registry — stable IDs, heap-slot-stable, ref-counted so shared
    // textures (e.g. one albedo used by many materials) are not double-allocated.

    private static readonly Dictionary<ulong, AssetTextureEntry> _textures = new();
    private static ulong _nextTextureId = 1;

    public static ulong RegisterTexture(RhiTexture texture, uint heapSlot, int initialRefCount = 1)
    {
        ArgumentNullException.ThrowIfNull(texture);
        lock (_lock)
        {
            ulong id = _nextTextureId++;
            _textures[id] = new AssetTextureEntry
            {
                Id = id,
                Texture = texture,
                HeapSlot = heapSlot,
                RefCount = initialRefCount,
            };
            return id;
        }
    }

    public static AssetTextureEntry? GetTexture(ulong id)
    {
        lock (_lock) return _textures.TryGetValue(id, out var entry) ? entry : null;
    }

    public static void AddRefTexture(ulong id)
    {
        lock (_lock) { if (_textures.TryGetValue(id, out var entry)) entry.RefCount++; }
    }

    public static void ReleaseTexture(ulong id)
    {
        lock (_lock)
        {
            if (!_textures.TryGetValue(id, out var entry)) return;
            entry.RefCount--;
            if (entry.RefCount > 0) return;
            entry.Texture?.Dispose();
            _textures.Remove(id);
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _meshes.Clear();
            _materials.Clear();
            _models.Clear();
            _textures.Clear();
        }
    }
}

