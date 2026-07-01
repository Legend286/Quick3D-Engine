// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Engine.RHI;

namespace Engine.Assets;

public class MaterialDefinition
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("albedo_color")]
    public float[] AlbedoColor { get; set; } = { 1, 1, 1, 1 };

    [JsonPropertyName("albedo_texture")]
    public string? AlbedoTexture { get; set; }

    [JsonPropertyName("normal_texture")]
    public string? NormalTexture { get; set; }

    [JsonPropertyName("rma_texture")]
    public string? RmaTexture { get; set; }

    [JsonPropertyName("metallic")]
    public float Metallic { get; set; } = 0.0f;

    [JsonPropertyName("roughness")]
    public float Roughness { get; set; } = 1.0f;
}

public class Material
{
    public float[] AlbedoColor { get; set; } = { 1, 1, 1, 1 };
    public RhiTexture? AlbedoTexture { get; set; }
    public RhiTexture? NormalTexture { get; set; }
    public RhiTexture? RmaTexture { get; set; }
    public float Metallic { get; set; }
    public float Roughness { get; set; }
}

public static class MaterialLoader
{
    public static Material LoadMat(RhiDevice device, string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Material file not found: {path}");

        string json = File.ReadAllText(path);
        var def = JsonSerializer.Deserialize<MaterialDefinition>(json);
        if (def == null)
            throw new InvalidDataException("Failed to parse .mat");

        var mat = new Material
        {
            AlbedoColor = def.AlbedoColor,
            Metallic = def.Metallic,
            Roughness = def.Roughness
        };

        var dir = Path.GetDirectoryName(path) ?? "";

        if (!string.IsNullOrEmpty(def.AlbedoTexture))
        {
            mat.AlbedoTexture = TextureLoader.LoadTexture(device, Path.Combine(dir, def.AlbedoTexture));
        }

        if (!string.IsNullOrEmpty(def.NormalTexture))
        {
            mat.NormalTexture = TextureLoader.LoadTexture(device, Path.Combine(dir, def.NormalTexture));
        }

        if (!string.IsNullOrEmpty(def.RmaTexture))
        {
            mat.RmaTexture = TextureLoader.LoadTexture(device, Path.Combine(dir, def.RmaTexture));
        }

        return mat;
    }
}
