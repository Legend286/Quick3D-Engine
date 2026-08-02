// SPDX-License-Identifier: MIT
using System;
using System.IO;
using Engine.Scene;
using Xunit;

namespace Engine.Game.Tests;

/// <summary>Verifies scene files remain reloadable after editor persistence.</summary>
public sealed class ScenePersistenceTests
{
    [Fact]
    public void Load_PrefersScenesDirectoryForNamedScene()
    {
        string contentRoot = Path.Combine(
            Path.GetTempPath(),
            $"quick3d-scenes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(contentRoot, "scenes"));

        try
        {
            File.WriteAllText(
                Path.Combine(contentRoot, "example.scene.json"),
                "{ \"name\": \"direct\" }");
            File.WriteAllText(
                Path.Combine(contentRoot, "scenes", "example.scene.json"),
                "{ \"name\": \"scenes\" }");

            SceneGraph scene = new SceneLoader(contentRoot).Load("example");

            Assert.Equal("scenes", scene.Name);
            Assert.Single(scene.Passes);
            Assert.Equal("PbrPass", scene.Passes[0].Name);
        }
        finally
        {
            if (Directory.Exists(contentRoot))
                Directory.Delete(contentRoot, recursive: true);
        }
    }

    [Fact]
    public void Load_NormalizesLegacyEmptySceneToPbrPass()
    {
        string contentRoot = Path.Combine(
            Path.GetTempPath(),
            $"quick3d-empty-scene-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(contentRoot, "scenes"));
        string scenePath = Path.Combine(
            contentRoot,
            "scenes",
            "empty.scene.json");

        try
        {
            File.WriteAllText(scenePath, "{ \"name\": \"empty\" }");

            SceneGraph scene = new SceneLoader(contentRoot).Load("empty");

            Assert.Single(scene.Passes);
            Assert.Equal("PbrPass", scene.Passes[0].Name);
            Assert.Equal("shaders/pbr.slang", scene.Passes[0].ShaderVertex);
            Assert.Equal("shaders/pbr.slang", scene.Passes[0].ShaderFragment);
        }
        finally
        {
            if (Directory.Exists(contentRoot))
                Directory.Delete(contentRoot, recursive: true);
        }
    }
}
