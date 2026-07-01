// SPDX-License-Identifier: MIT
using System.Collections.Generic;

namespace Engine.Assets;

public static class AssetRegistry
{
    private static readonly Dictionary<ulong, Mesh> _meshes = new();
    private static ulong _nextMeshId = 1;

    public static ulong RegisterMesh(Mesh mesh)
    {
        ulong id = _nextMeshId++;
        _meshes[id] = mesh;
        return id;
    }

    public static Mesh? GetMesh(ulong id)
    {
        if (_meshes.TryGetValue(id, out Mesh? mesh))
            return mesh;
        return null;
    }

    private static readonly Dictionary<ulong, Material> _materials = new();
    private static ulong _nextMaterialId = 1;

    public static ulong RegisterMaterial(Material mat)
    {
        ulong id = _nextMaterialId++;
        _materials[id] = mat;
        return id;
    }

    public static Material? GetMaterial(ulong id)
    {
        if (_materials.TryGetValue(id, out Material? mat))
            return mat;
        return null;
    }

    private static readonly Dictionary<ulong, Model> _models = new();
    private static ulong _nextModelId = 1;

    public static ulong RegisterModel(Model model)
    {
        ulong id = _nextModelId++;
        _models[id] = model;
        return id;
    }

    public static Model? GetModel(ulong id)
    {
        if (_models.TryGetValue(id, out Model? model))
            return model;
        return null;
    }
}
