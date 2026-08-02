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
    /// <summary>Gets or sets the imported part name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("mesh")]
    public string Mesh { get; set; } = "";

    [JsonPropertyName("material")]
    public string Material { get; set; } = "";

    [JsonPropertyName("bounds")]
    public ModelPartBounds? Bounds { get; set; }

    /// <summary>Gets or sets the part centre relative to the model origin.</summary>
    [JsonPropertyName("local_offset")]
    public float[] LocalOffset { get; set; } =
        new float[3];

    /// <summary>Gets or sets the translation applied after GPU skinning.</summary>
    [JsonPropertyName("skinned_output_offset")]
    public float[] SkinnedOutputOffset { get; set; } =
        new float[3];
}

public class ModelDefinition
{
    [JsonPropertyName("version")]
    public int Version { get; set; }

    /// <summary>Cooked skeleton sidecar relative to this model file, or empty.</summary>
    [JsonPropertyName("skeleton")]
    public string Skeleton { get; set; } = "";

    /// <summary>Cooked animation sidecar relative to this model file, or empty.</summary>
    [JsonPropertyName("animation")]
    public string Animation { get; set; } = "";

    [JsonPropertyName("parts")]
    public ModelPartDefinition[] Parts { get; set; } = Array.Empty<ModelPartDefinition>();

    [JsonPropertyName("bounds")]
    public ModelPartBounds? Bounds { get; set; }
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
    public Vector3 BoundsSphereCenter;
    public float BoundsSphereRadius;
    public Vector3 LocalOffset;
    public Vector3 SkinnedOutputOffset;
}

public class Model
{
    public string SourcePath { get; set; } = string.Empty;
    /// <summary>
    /// Gets or sets the original `.mdl` part represented by this model, or -1.
    /// </summary>
    public int SourcePartIndex { get; set; } = -1;
    /// <summary>Resolved skeleton sidecar path next to the model, or empty.</summary>
    public string SkeletonPath { get; set; } = string.Empty;
    /// <summary>Resolved animation sidecar path next to the model, or empty.</summary>
    public string AnimationPath { get; set; } = string.Empty;
    public ModelPart[] Parts { get; set; } = Array.Empty<ModelPart>();
}

public static class ModelLoader
{
    /// <summary>
    /// Reads model metadata without creating GPU resources.
    /// </summary>
    public static ModelDefinition ReadDefinition(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Model file not found: {path}");

        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ModelDefinition>(json)
            ?? throw new InvalidDataException("Failed to parse .mdl");
    }

    /// <summary>
    /// Calculates a conservative sphere from whole-model or part bounds.
    /// </summary>
    public static (Vector3 Center, float Radius) GetBoundingSphere(
        Model model,
        int partIndex = -1)
    {
        if (model.Parts.Length == 0)
            return (Vector3.Zero, 0.5f);
        if (partIndex >= model.Parts.Length)
            throw new ArgumentOutOfRangeException(nameof(partIndex));

        if (partIndex >= 0)
            return GetPartBoundingSphere(
                model.Parts[partIndex]);

        (Vector3 center, float radius) =
            GetPartBoundingSphere(model.Parts[0]);
        for (int index = 1; index < model.Parts.Length; ++index)
        {
            (Vector3 partCenter, float partRadius) =
                GetPartBoundingSphere(model.Parts[index]);
            Vector3 offset = partCenter - center;
            float distance = offset.Length();
            if (distance + partRadius <= radius)
                continue;
            if (distance + radius <= partRadius)
            {
                center = partCenter;
                radius = partRadius;
                continue;
            }
            if (distance <= 0.0f)
            {
                radius = MathF.Max(radius, partRadius);
                continue;
            }

            float mergedRadius =
                (distance + radius + partRadius) * 0.5f;
            center += offset *
                ((mergedRadius - radius) / distance);
            radius = mergedRadius;
        }

        return (center, radius);
    }

    private static (Vector3 Center, float Radius)
        GetPartBoundingSphere(ModelPart part)
    {
        if (part.BoundsSphereRadius > 0.0f)
        {
            return (
                part.BoundsSphereCenter +
                    part.LocalOffset,
                part.BoundsSphereRadius);
        }

        Vector3 center =
            (part.BoundsMin + part.BoundsMax) * 0.5f;
        return (
            center + part.LocalOffset,
            MathF.Max(
                Vector3.Distance(center, part.BoundsMax),
                0.001f));
    }

    /// <summary>
    /// Creates a model view containing one stable source part.
    /// </summary>
    /// <summary>
    /// Resolves the animation sidecar for a model: the embedded `.mdl`
    /// reference first, then the same-basename convention, then a scan of
    /// sibling sidecars. The legacy cook named the `.mdl` after the glTF
    /// root node while naming `.skel`/`.anim` after the source file stem,
    /// so the same-basename lookup alone misses divergently-named assets.
    /// </summary>
    public static string? ResolveAnimationSidecar(
        string mdlPath,
        Model model)
    {
        if (!string.IsNullOrWhiteSpace(model.AnimationPath) &&
            File.Exists(model.AnimationPath))
        {
            return Path.GetFullPath(model.AnimationPath);
        }

        string legacy = Path.ChangeExtension(mdlPath, ".anim");
        if (File.Exists(legacy))
            return Path.GetFullPath(legacy);

        string? directory = Path.GetDirectoryName(mdlPath);
        if (string.IsNullOrEmpty(directory))
            return null;

        string? skelStem = null;
        string[] skeletons = Directory.GetFiles(directory, "*.skel");
        if (skeletons.Length == 1)
            skelStem = Path.GetFileNameWithoutExtension(skeletons[0]);

        string[] animations = Directory.GetFiles(directory, "*.anim");
        if (animations.Length == 0)
            return null;
        if (animations.Length == 1)
            return Path.GetFullPath(animations[0]);
        if (skelStem != null)
        {
            foreach (string animation in animations)
            {
                if (string.Equals(
                        Path.GetFileNameWithoutExtension(animation),
                        skelStem,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetFullPath(animation);
                }
            }
        }
        return null;
    }

    public static Model SelectPart(Model source, int partIndex)
    {
        if ((uint)partIndex >= (uint)source.Parts.Length)
            throw new ArgumentOutOfRangeException(nameof(partIndex));
        ModelPart part = source.Parts[partIndex];
        part.LocalOffset = Vector3.Zero;
        return new Model
        {
            SourcePath = source.SourcePath,
            SourcePartIndex = partIndex,
            SkeletonPath = source.SkeletonPath,
            AnimationPath = source.AnimationPath,
            Parts = [part]
        };
    }

    public static Model LoadMdl(RhiDevice device, string path)
    {
        ModelDefinition def = ReadDefinition(path);

        string? modelDirectory = Path.GetDirectoryName(path);
        var model = new Model
        {
            SourcePath = path,
            Parts = new ModelPart[def.Parts.Length]
        };
        if (!string.IsNullOrEmpty(def.Skeleton))
        {
            model.SkeletonPath = Path.Combine(
                modelDirectory ?? "",
                def.Skeleton);
        }
        if (!string.IsNullOrEmpty(def.Animation))
        {
            model.AnimationPath = Path.Combine(
                modelDirectory ?? "",
                def.Animation);
        }

        for (int i = 0; i < def.Parts.Length; i++)
        {
            var partDef = def.Parts[i];
            var part = new ModelPart();
            if (partDef.LocalOffset.Length >= 3)
            {
                part.LocalOffset = new Vector3(
                    partDef.LocalOffset[0],
                    partDef.LocalOffset[1],
                    partDef.LocalOffset[2]);
            }
            if (partDef.SkinnedOutputOffset.Length >= 3)
            {
                part.SkinnedOutputOffset = new Vector3(
                    partDef.SkinnedOutputOffset[0],
                    partDef.SkinnedOutputOffset[1],
                    partDef.SkinnedOutputOffset[2]);
            }
            
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
            else if (def.Bounds != null)
            {
                part.BoundsMin = new Vector3(def.Bounds.Min[0], def.Bounds.Min[1], def.Bounds.Min[2]);
                part.BoundsMax = new Vector3(def.Bounds.Max[0], def.Bounds.Max[1], def.Bounds.Max[2]);
            }
            else
            {
                part.BoundsMin = new Vector3(-1, -1, -1);
                part.BoundsMax = new Vector3(1, 1, 1);
            }
            if (part.Mesh != null)
            {
                part.BoundsSphereCenter =
                    part.Mesh.BoundsSphereCenter;
                part.BoundsSphereRadius =
                    part.Mesh.BoundsSphereRadius;
            }

            model.Parts[i] = part;
        }

        return model;
    }
}
