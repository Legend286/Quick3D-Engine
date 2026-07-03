// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Engine.RHI;

using System.Numerics;

namespace Engine.Assets;

public class ModelPartBounds
{
    [JsonPropertyName("min")]
    public float[] Min { get; set; } = new float[3];

    [JsonPropertyName("max")]
    public float[] Max { get; set; } = new float[3];
}

public class ModelPartDefinition
{
    [JsonPropertyName("mesh")]
    public string Mesh { get; set; } = "";

    [JsonPropertyName("material")]
    public string Material { get; set; } = "";

    [JsonPropertyName("bounds")]
    public ModelPartBounds? Bounds { get; set; }
}

public class ModelDefinition
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("parts")]
    public ModelPartDefinition[] Parts { get; set; } = Array.Empty<ModelPartDefinition>();
}

public struct ModelPart
{
    public ulong MeshId;
    public ulong MaterialId;
    // We can also store the direct references if we want, but IDs are better
    public Mesh Mesh;
    public Material Material;
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;
}

public class Model
{
    public string SourcePath { get; set; } = string.Empty;
    public ModelPart[] Parts { get; set; } = Array.Empty<ModelPart>();
}

public static class ModelLoader
{
    public static Model LoadMdl(RhiDevice device, string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Model file not found: {path}");

        string json = File.ReadAllText(path);
        var def = JsonSerializer.Deserialize<ModelDefinition>(json);
        if (def == null)
            throw new InvalidDataException("Failed to parse .mdl");

        var model = new Model
        {
            SourcePath = path,
            Parts = new ModelPart[def.Parts.Length]
        };

        for (int i = 0; i < def.Parts.Length; i++)
        {
            var partDef = def.Parts[i];
            var part = new ModelPart();
            
            if (!string.IsNullOrEmpty(partDef.Mesh))
            {
                try
                {
                    part.Mesh = MeshLoader.LoadMsh(device, Path.Combine(Path.GetDirectoryName(path) ?? "", partDef.Mesh));
                    part.MeshId = AssetRegistry.RegisterMesh(part.Mesh);
                }
                catch (Exception ex)
                {
                    Engine.CBindings.Log.Error($"[ModelLoader] Failed to load mesh '{partDef.Mesh}': {ex.Message}", "Assets");
                }
            }
                
            if (!string.IsNullOrEmpty(partDef.Material))
            {
                try
                {
                    part.Material = MaterialLoader.LoadMat(device, Path.Combine(Path.GetDirectoryName(path) ?? "", partDef.Material));
                    part.MaterialId = AssetRegistry.RegisterMaterial(part.Material);
                }
                catch (Exception ex)
                {
                    Engine.CBindings.Log.Warn($"[ModelLoader] Missing material '{partDef.Material}', using fallback. ({ex.Message})", "Assets");
                    part.Material = new Material 
                    { 
                        AlbedoColor = new float[] { 1.0f, 0.0f, 1.0f, 1.0f },
                        EmissiveColor = new float[] { 1.0f, 0.0f, 1.0f, 1.0f },
                    };
                    part.MaterialId = AssetRegistry.RegisterMaterial(part.Material);
                }
            }
                
            if (partDef.Bounds != null)
            {
                part.BoundsMin = new Vector3(partDef.Bounds.Min[0], partDef.Bounds.Min[1], partDef.Bounds.Min[2]);
                part.BoundsMax = new Vector3(partDef.Bounds.Max[0], partDef.Bounds.Max[1], partDef.Bounds.Max[2]);
            }
            else
            {
                part.BoundsMin = new Vector3(-1, -1, -1);
                part.BoundsMax = new Vector3(1, 1, 1);
            }

            model.Parts[i] = part;
        }

        return model;
    }
}
