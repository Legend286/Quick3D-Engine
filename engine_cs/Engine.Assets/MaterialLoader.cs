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

    [JsonPropertyName("subsurface")]
    public float Subsurface { get; set; } = 0.0f;

    [JsonPropertyName("subsurface_radius")]
    public float[] SubsurfaceRadius { get; set; } = { 1.0f, 0.2f, 0.1f };

    [JsonPropertyName("subsurface_color")]
    public float[] SubsurfaceColor { get; set; } = { 1.0f, 1.0f, 1.0f };

    [JsonPropertyName("clearcoat")]
    public float Clearcoat { get; set; } = 0.0f;

    [JsonPropertyName("clearcoat_roughness")]
    public float ClearcoatRoughness { get; set; } = 0.0f;

    [JsonPropertyName("top_color")]
    public float[] TopColor { get; set; } = { 1, 1, 1, 1 };

    [JsonPropertyName("top_metallic")]
    public float TopMetallic { get; set; } = 0.0f;

    [JsonPropertyName("top_roughness")]
    public float TopRoughness { get; set; } = 1.0f;

    [JsonPropertyName("top_mask_type")]
    public uint TopMaskType { get; set; } = 0;
}

public class Material
{
    public float[] AlbedoColor { get; set; } = { 1, 1, 1, 1 };
    public float[] EmissiveColor { get; set; } = { 0, 0, 0, 1 };
    public RhiTexture? AlbedoTexture { get; set; }
    public RhiTexture? NormalTexture { get; set; }
    public RhiTexture? RmaTexture { get; set; }
    public float Metallic { get; set; }
    public float Roughness { get; set; }
    public float Subsurface { get; set; } = 0.0f;
    public float[] SubsurfaceRadius { get; set; } = { 1.0f, 0.2f, 0.1f };
    public float[] SubsurfaceColor  { get; set; } = { 1.0f, 1.0f, 1.0f };
    public float Clearcoat { get; set; }
    public float ClearcoatRoughness { get; set; }
    public float[] TopColor { get; set; } = { 1, 1, 1, 1 };
    public float TopMetallic { get; set; }
    public float TopRoughness { get; set; } = 1.0f;
    public uint TopMaskType { get; set; }
}

public static class MaterialLoader
{
    private static readonly System.Collections.Generic.Dictionary<string, Material> _cache = new();

    public static void ClearCache() => _cache.Clear();

    public static Material LoadMat(RhiDevice device, string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Material file not found: {path}");

        string fullPath = Path.GetFullPath(path);
        if (_cache.TryGetValue(fullPath, out var cached)) return cached;

        string json = File.ReadAllText(path);
        var def = JsonSerializer.Deserialize<MaterialDefinition>(json);
        if (def == null)
            throw new InvalidDataException("Failed to parse .mat");

        var mat = new Material
        {
            AlbedoColor = def.AlbedoColor,
            Metallic    = def.Metallic,
            Roughness   = def.Roughness,
            Subsurface       = def.Subsurface,
            SubsurfaceRadius = def.SubsurfaceRadius,
            SubsurfaceColor  = def.SubsurfaceColor,
            Clearcoat        = def.Clearcoat,
            ClearcoatRoughness = def.ClearcoatRoughness,
            TopColor         = def.TopColor,
            TopMetallic      = def.TopMetallic,
            TopRoughness     = def.TopRoughness,
            TopMaskType      = def.TopMaskType,
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

        _cache[fullPath] = mat;
        return mat;
    }
}
