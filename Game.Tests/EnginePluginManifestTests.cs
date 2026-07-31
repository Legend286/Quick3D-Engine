// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using System.Text.Json;
using Engine.Plugins;
using Xunit;

namespace Engine.Game.Tests;

public sealed class EnginePluginManifestTests
{
    [Fact]
    public void Deserialize_WithShaderIncludes_ParsesArray()
    {
        const string json = """
        {
          "version": 1,
          "id": "core.test.shader-includes",
          "name": "Shader Includes Probe",
          "description": "Verifies the shader_includes schema field parses as List<string>.",
          "plugin_version": "1.0.0",
          "kind": "Renderer",
          "required": false,
          "assembly": "Test.dll",
          "entry_point": "Test.Entry",
          "shader_files": ["shaders/test.slang"],
          "shader_includes": ["shaders/", "shaders/inc/"]
        }
        """;

        EnginePluginManifest? manifest =
            JsonSerializer.Deserialize<EnginePluginManifest>(json);

        Assert.NotNull(manifest);
        Assert.Equal(
            new[] { "shaders/", "shaders/inc/" },
            manifest!.ShaderIncludes);
    }

    [Fact]
    public void Deserialize_MissingShaderIncludes_DefaultsToEmptyList()
    {
        const string json = """
        {
          "version": 1,
          "id": "core.test.no-shader-includes"
        }
        """;

        EnginePluginManifest? manifest =
            JsonSerializer.Deserialize<EnginePluginManifest>(json);

        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.ShaderIncludes);
        Assert.Empty(manifest.ShaderIncludes);
    }

    [Fact]
    public void Deserialize_ShaderFilesAlone_DoesNotPolluteShaderIncludes()
    {
        const string json = """
        {
          "version": 1,
          "id": "core.test.shader-files-only",
          "shader_files": ["shaders/test.slang"]
        }
        """;

        EnginePluginManifest? manifest =
            JsonSerializer.Deserialize<EnginePluginManifest>(json);

        Assert.NotNull(manifest);
        Assert.Empty(manifest!.ShaderIncludes);
        Assert.Single(manifest.ShaderFiles);
    }

    [Fact]
    public void Serialize_ShaderIncludesAppearsAsTopLevelArray()
    {
        EnginePluginManifest manifest = new()
        {
            Id = "core.test.serialize",
            ShaderIncludes = new List<string> { "shaders/", "include/" },
        };

        string json = JsonSerializer.Serialize(manifest);

        Assert.Contains("\"shader_includes\":", json);
        Assert.Contains("shaders/", json);
        Assert.Contains("include/", json);
    }

    [Fact]
    public void NewManifest_ShaderIncludes_IsEmptyByDefault()
    {
        EnginePluginManifest manifest = new()
        {
            Id = "core.test.default",
        };

        Assert.NotNull(manifest.ShaderIncludes);
        Assert.Empty(manifest.ShaderIncludes);
    }
}
