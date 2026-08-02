// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Engine.RHI;
using Engine.Scene.Components;

namespace Engine.Scene;

public static class SceneSaver
{
    public static void Save(
        IEntityStore world,
        SceneGraph baseScene,
        string path,
        string? contentRoot = null)
    {
        // We only overwrite the Models and Lights lists. We retain the Passes from the baseScene.
        baseScene.Models.Clear();
        baseScene.Lights.Clear();

        foreach (var entity in world.Entities)
        {
            if (world.TryGet<ProceduralDemoEntityComponent>(entity, out _))
                continue;
            if (world.TryGet<Transform>(entity, out var transform))
            {
                if (world.TryGet<ModelComponent>(entity, out var modelComponent))
                {
                    var model = Engine.Assets.AssetRegistry.GetModel(modelComponent.ModelId);
                    if (model != null)
                    {
                        var modelRef = new ModelRef();
                        // Get the relative path for Source. Example: "Content/models/foo.mdl" -> "models/foo.mdl"
                        modelRef.Source = NormalizeAssetSource(
                            model.SourcePath,
                            contentRoot);
                        modelRef.Name = Path.GetFileNameWithoutExtension(
                            modelRef.Source);
                        
                        modelRef.Position = new float[] { transform.Position.X, transform.Position.Y, transform.Position.Z };
                        modelRef.Rotation = new float[] { transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W };
                        modelRef.Scale = new float[] { transform.Scale.X, transform.Scale.Y, transform.Scale.Z };
                        modelRef.StaticShadowCaster =
                            modelComponent.StaticShadowCaster;
                        modelRef.PartIndex =
                            model.SourcePartIndex >= 0
                                ? model.SourcePartIndex
                                : null;
                        if (world.TryGet<AnimatorComponent>(entity, out _))
                        {
                            modelRef.AnimationSource =
                                !string.IsNullOrWhiteSpace(
                                    model.AnimationPath)
                                    ? NormalizeAssetSource(
                                        model.AnimationPath,
                                        contentRoot)
                                    : Path.ChangeExtension(
                                        modelRef.Source,
                                        ".anim");
                        }

                        baseScene.Models.Add(modelRef);
                    }
                }
                else if (world.TryGet<DirectionalLightComponent>(entity, out var lightComp))
                {
                    var lightNode = new LightNode
                    {
                        Type = "directional",
                        Position = new float[] { transform.Position.X, transform.Position.Y, transform.Position.Z },
                        Direction = new float[] { lightComp.Direction.X, lightComp.Direction.Y, lightComp.Direction.Z },
                        Color = new float[] { lightComp.Color.X, lightComp.Color.Y, lightComp.Color.Z },
                        Intensity = lightComp.Intensity,
                        SunRadius = lightComp.AngularRadius,
                        CastShadows = lightComp.CastShadows
                    };

                    baseScene.Lights.Add(lightNode);
                }
                else if (world.TryGet<PointLightComponent>(entity, out var pointLight))
                {
                    var lightNode = new LightNode
                    {
                        Type = "point",
                        Position = new float[] { transform.Position.X, transform.Position.Y, transform.Position.Z },
                        Direction = new float[] { 0, -1, 0 },
                        Color = new float[] { pointLight.Color.X, pointLight.Color.Y, pointLight.Color.Z },
                        Intensity = pointLight.Intensity,
                        Range = pointLight.Range,
                        SourceRadius = pointLight.SourceRadius,
                        CastShadows = pointLight.CastShadows
                    };

                    baseScene.Lights.Add(lightNode);
                }
                else if (world.TryGet<SpotLightComponent>(entity, out var spotLight))
                {
                    var spotDirection = LightMath.GetSpotDirection(transform.Rotation);
                    var lightNode = new LightNode
                    {
                        Type = "spot",
                        Position = new float[] { transform.Position.X, transform.Position.Y, transform.Position.Z },
                        Direction = new float[] { spotDirection.X, spotDirection.Y, spotDirection.Z },
                        Color = new float[] { spotLight.Color.X, spotLight.Color.Y, spotLight.Color.Z },
                        Intensity = spotLight.Intensity,
                        Range = spotLight.Range,
                        InnerCone = spotLight.InnerCone,
                        OuterCone = spotLight.OuterCone,
                        SourceRadius = spotLight.SourceRadius,
                        CastShadows = spotLight.CastShadows
                    };

                    baseScene.Lights.Add(lightNode);
                }
            }
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(baseScene, options);

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string tempPath = path + ".tmp";
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        using (FileStream stream = new(
                   tempPath,
                   FileMode.Create,
                   FileAccess.Write,
                   FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
        }
        File.Move(tempPath, path, overwrite: true);
    }

    private static string NormalizeAssetSource(
        string source,
        string? contentRoot)
    {
        if (string.IsNullOrWhiteSpace(source))
            return string.Empty;

        string normalized = source.Replace('\\', '/');
        if (Path.IsPathRooted(source))
        {
            string fullSource = Path.GetFullPath(source);
            if (!string.IsNullOrWhiteSpace(contentRoot))
            {
                string fullRoot = Path.GetFullPath(contentRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string rootPrefix = fullRoot + Path.DirectorySeparatorChar;
                if (fullSource.StartsWith(
                        rootPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetRelativePath(fullRoot, fullSource)
                        .Replace('\\', '/');
                }
            }
            return normalized;
        }

        if (normalized.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
            return normalized["Content/".Length..];
        return normalized;
    }
}
