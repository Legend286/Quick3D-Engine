// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Engine.RHI;
using Engine.Scene.Components;

namespace Engine.Scene;

public static class SceneSaver
{
    public static void Save(IEntityStore world, SceneGraph baseScene, string path)
    {
        // We only overwrite the Models and Lights lists. We retain the Passes from the baseScene.
        baseScene.Models.Clear();
        baseScene.Lights.Clear();

        foreach (var entity in world.Entities)
        {
            if (world.TryGet<Transform>(entity, out var transform))
            {
                if (world.TryGet<ModelComponent>(entity, out var modelComponent))
                {
                    var model = Engine.Assets.AssetRegistry.GetModel(modelComponent.ModelId);
                    if (model != null)
                    {
                        var modelRef = new ModelRef();
                        // Get the relative path for Source. Example: "Content/models/foo.mdl" -> "models/foo.mdl"
                        var source = model.SourcePath;
                        if (source.StartsWith("Content/") || source.StartsWith("Content\\"))
                        {
                            source = source.Substring(8);
                        }
                        
                        modelRef.Source = source.Replace('\\', '/');
                        modelRef.Name = Path.GetFileNameWithoutExtension(source);
                        
                        modelRef.Position = new float[] { transform.Position.X, transform.Position.Y, transform.Position.Z };
                        modelRef.Rotation = new float[] { transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W };
                        modelRef.Scale = new float[] { transform.Scale.X, transform.Scale.Y, transform.Scale.Z };

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
                        Range = 100.0f
                    };
                    
                    baseScene.Lights.Add(lightNode);
                }
            }
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(baseScene, options);

        // Atomic write
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }
}
