// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Text.Json;
using Engine.Assets;
using Engine.RHI;
using Engine.Scene;
using Engine.Scene.Components;
using Xunit;

namespace Engine.Game.Tests;

/// <summary>Verifies explicit animation references survive scene serialization.</summary>
public sealed class SceneAnimationPersistenceTests
{
    [Fact]
    public void ModelRef_AnimationSourceRoundTripsAndLegacyValueIsAbsent()
    {
        SceneGraph scene = new();
        scene.Models.Add(new ModelRef
        {
            Name = "animated",
            Source = "models/character.mdl",
            AnimationSource = "models/character.anim",
        });

        string json = JsonSerializer.Serialize(scene);
        SceneGraph? roundTripped = JsonSerializer.Deserialize<SceneGraph>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal("models/character.anim", roundTripped!.Models[0].AnimationSource);

        SceneGraph? legacy = JsonSerializer.Deserialize<SceneGraph>("""
        {
          "models": [
            { "name": "static", "source": "models/prop.mdl" }
          ]
        }
        """);

        Assert.NotNull(legacy);
        Assert.Null(legacy!.Models[0].AnimationSource);
    }

    [Fact]
    public void SceneSaver_NormalizesAbsoluteContentAssetPath()
    {
        AssetRegistry.Clear();
        using EcsWorld world = new();
        string contentRoot = Path.Combine(
            Path.GetTempPath(),
            $"quick3d-content-{Guid.NewGuid():N}");
        string outputPath = Path.Combine(
            contentRoot,
            "scenes",
            "asset-path.scene.json");
        Directory.CreateDirectory(Path.Combine(contentRoot, "models"));

        try
        {
            ulong modelId = AssetRegistry.RegisterModel(new Model
            {
                SourcePath = Path.Combine(
                    contentRoot,
                    "models",
                    "character.mdl"),
            });
            ulong entity = world.CreateEntity();
            world.Set(entity, Transform.Default);
            world.Set(entity, ModelComponent.Create(modelId));

            SceneSaver.Save(
                world,
                new SceneGraph { Name = "asset-path" },
                outputPath,
                contentRoot);

            SceneGraph? saved = JsonSerializer.Deserialize<SceneGraph>(
                File.ReadAllText(outputPath));
            Assert.NotNull(saved);
            Assert.Single(saved!.Models);
            Assert.Equal("models/character.mdl", saved.Models[0].Source);
        }
        finally
        {
            AssetRegistry.Clear();
            if (Directory.Exists(contentRoot))
                Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public void SceneSaver_PreservesPausedAnimatorReference()
    {
        AssetRegistry.Clear();
        using EcsWorld world = new();
        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"quick3d-scene-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        string outputPath = Path.Combine(
            tempDirectory,
            "paused-animation.scene.json");

        try
        {
            ulong modelId = AssetRegistry.RegisterModel(new Model
            {
                SourcePath = "Content/models/character.mdl",
            });
            ulong entity = world.CreateEntity();
            world.Set(entity, new Transform());
            world.Set(entity, ModelComponent.Create(modelId));
            world.Set(entity, new AnimatorComponent
            {
                SkeletonId = 1,
                BaseClipId = 1,
                PlaybackRate = 1.0f,
                Flags = 0,
                Generation = 1,
            });

            SceneSaver.Save(
                world,
                new SceneGraph { Name = "paused-animation" },
                outputPath,
                contentRoot: null);

            SceneGraph? saved = JsonSerializer.Deserialize<SceneGraph>(
                File.ReadAllText(outputPath));
            Assert.NotNull(saved);
            Assert.Single(saved!.Models);
            Assert.Equal(
                "models/character.anim",
                saved.Models[0].AnimationSource);
        }
        finally
        {
            AssetRegistry.Clear();
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
