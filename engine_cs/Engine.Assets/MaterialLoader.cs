// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Engine.RHI;

using System.Collections.Generic;

namespace Engine.Assets;


public class MaterialLayerDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Layer";

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

    [JsonPropertyName("mask_type")]
    public uint MaskType { get; set; } = 0; // 0=None, 1=3D Noise, 2=Cavity, 3=Height, 4=TextureMask

    [JsonPropertyName("mask_texture")]
    public string? MaskTexture { get; set; }

    [JsonPropertyName("noise_scale")]
    public float NoiseScale { get; set; } = 10.0f;

    [JsonPropertyName("noise_detail")]
    public int NoiseDetail { get; set; } = 3;

    [JsonPropertyName("noise_threshold")]
    public float NoiseThreshold { get; set; } = 0.5f;
}


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

    [JsonPropertyName("top_mask_texture")]
    public string? TopMaskTexture { get; set; }

    [JsonPropertyName("noise_scale")]
    public float NoiseScale { get; set; } = 10.0f;

    [JsonPropertyName("noise_threshold_min")]
    public float NoiseThresholdMin { get; set; } = 0.3f;

    [JsonPropertyName("noise_threshold_max")]
    public float NoiseThresholdMax { get; set; } = 0.7f;

    [JsonPropertyName("layers")]
    public List<MaterialLayerDefinition> Layers { get; set; } = new();
}

public class MaterialLayer
{
    public string Name { get; set; } = "Layer";
    public float[] AlbedoColor { get; set; } = { 1, 1, 1, 1 };
    public RhiTexture? AlbedoTexture { get; set; }
    public string? AlbedoTexturePath { get; set; }
    public RhiTexture? NormalTexture { get; set; }
    public string? NormalTexturePath { get; set; }
    public RhiTexture? RmaTexture { get; set; }
    public string? RmaTexturePath { get; set; }
    public float Metallic { get; set; } = 0.0f;
    public float Roughness { get; set; } = 1.0f;
    public uint MaskType { get; set; } = 0;
    public float NoiseScale { get; set; } = 10.0f;
    public int NoiseDetail { get; set; } = 3;
    public float NoiseThreshold { get; set; } = 0.5f;
    public RhiTexture? MaskTexture { get; set; }
    public string? MaskTexturePath { get; set; }
}

public class Material
{
    public float[] AlbedoColor { get; set; } = { 1, 1, 1, 1 };
    public float[] EmissiveColor { get; set; } = { 0, 0, 0, 1 };
    public RhiTexture? AlbedoTexture { get; set; }
    public string? AlbedoTexturePath { get; set; }
    public RhiTexture? NormalTexture { get; set; }
    public string? NormalTexturePath { get; set; }
    public RhiTexture? RmaTexture { get; set; }
    public string? RmaTexturePath { get; set; }
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
    public RhiTexture? TopMaskTexture { get; set; }
    public string? TopMaskTexturePath { get; set; }
    public float NoiseScale { get; set; } = 10.0f;
    public float NoiseThresholdMin { get; set; } = 0.3f;
    public float NoiseThresholdMax { get; set; } = 0.7f;

    // Secondary Layer (Layer 2)
    public float[] Layer2Color { get; set; } = { 1, 1, 1, 1 };
    public float Layer2Metallic { get; set; }
    public float Layer2Roughness { get; set; } = 1.0f;
    public uint Layer2MaskType { get; set; }
    public RhiTexture? Layer2MaskTexture { get; set; }
    public string? Layer2MaskTexturePath { get; set; }
    public float Layer2NoiseScale { get; set; } = 10.0f;
    public float Layer2NoiseThresholdMin { get; set; } = 0.3f;
    public float Layer2NoiseThresholdMax { get; set; } = 0.7f;

    public List<MaterialLayer> Layers { get; set; } = new();
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
            NoiseScale       = def.NoiseScale,
            NoiseThresholdMin = def.NoiseThresholdMin,
            NoiseThresholdMax = def.NoiseThresholdMax,
            AlbedoTexturePath = def.AlbedoTexture,
            NormalTexturePath = def.NormalTexture,
            RmaTexturePath = def.RmaTexture,
            TopMaskTexturePath = def.TopMaskTexture,
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

        if (!string.IsNullOrEmpty(def.TopMaskTexture))
        {
            mat.TopMaskTexture = TextureLoader.LoadTexture(device, Path.Combine(dir, def.TopMaskTexture));
        }

        if (def.Layers != null)
        {
            foreach (var ldef in def.Layers)
            {
                var layer = new MaterialLayer
                {
                    Name = ldef.Name,
                    AlbedoColor = ldef.AlbedoColor,
                    Metallic = ldef.Metallic,
                    Roughness = ldef.Roughness,
                    MaskType = ldef.MaskType,
                    NoiseScale = ldef.NoiseScale,
                    NoiseDetail = ldef.NoiseDetail,
                    NoiseThreshold = ldef.NoiseThreshold,
                    AlbedoTexturePath = ldef.AlbedoTexture,
                    NormalTexturePath = ldef.NormalTexture,
                    RmaTexturePath = ldef.RmaTexture,
                    MaskTexturePath = ldef.MaskTexture
                };
                if (!string.IsNullOrEmpty(ldef.AlbedoTexture)) layer.AlbedoTexture = TextureLoader.LoadTexture(device, Path.Combine(dir, ldef.AlbedoTexture));
                if (!string.IsNullOrEmpty(ldef.NormalTexture)) layer.NormalTexture = TextureLoader.LoadTexture(device, Path.Combine(dir, ldef.NormalTexture));
                if (!string.IsNullOrEmpty(ldef.RmaTexture)) layer.RmaTexture = TextureLoader.LoadTexture(device, Path.Combine(dir, ldef.RmaTexture));
                if (!string.IsNullOrEmpty(ldef.MaskTexture)) layer.MaskTexture = TextureLoader.LoadTexture(device, Path.Combine(dir, ldef.MaskTexture));
                mat.Layers.Add(layer);
            }
        }

        if (mat.Layers.Count > 0)
        {
            var l0 = mat.Layers[0];
            mat.TopColor = l0.AlbedoColor;
            mat.TopMetallic = l0.Metallic;
            mat.TopRoughness = l0.Roughness;
            mat.TopMaskType = l0.MaskType;
            mat.TopMaskTexture = l0.MaskTexture;
            mat.TopMaskTexturePath = l0.MaskTexturePath;
            mat.NoiseScale = l0.NoiseScale;
            mat.NoiseThresholdMin = Math.Max(0.0f, l0.NoiseThreshold - 0.2f);
            mat.NoiseThresholdMax = Math.Min(1.0f, l0.NoiseThreshold + 0.2f);
        }

        if (mat.Layers.Count > 1)
        {
            var l1 = mat.Layers[1];
            mat.Layer2Color = l1.AlbedoColor;
            mat.Layer2Metallic = l1.Metallic;
            mat.Layer2Roughness = l1.Roughness;
            mat.Layer2MaskType = l1.MaskType;
            mat.Layer2MaskTexture = l1.MaskTexture;
            mat.Layer2MaskTexturePath = l1.MaskTexturePath;
            mat.Layer2NoiseScale = l1.NoiseScale;
            mat.Layer2NoiseThresholdMin = Math.Max(0.0f, l1.NoiseThreshold - 0.2f);
            mat.Layer2NoiseThresholdMax = Math.Min(1.0f, l1.NoiseThreshold + 0.2f);
        }

        _cache[fullPath] = mat;
        return mat;
    }
}

