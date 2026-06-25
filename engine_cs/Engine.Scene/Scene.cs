// SPDX-License-Identifier: MIT
// Plain data types describing a scene loaded from a Content/scenes/*.scene.json.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Engine.Scene;

public sealed class Scene
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("passes")] public List<ScenePass> Passes { get; set; } = new();
    [JsonPropertyName("cameras")] public List<Camera> Cameras { get; set; } = new();
    [JsonPropertyName("meshes")] public List<MeshRef> Meshes { get; set; } = new();
    [JsonPropertyName("lights")] public List<DirectionalLight> Lights { get; set; } = new();
}

public sealed class ScenePass
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("shader_vs")] public string ShaderVertex { get; set; } = string.Empty;
    [JsonPropertyName("shader_fs")] public string ShaderFragment { get; set; } = string.Empty;
    [JsonPropertyName("entry")] public string Entry { get; set; } = "main0";
    [JsonPropertyName("clear_color")] public float[] ClearColor { get; set; } = new float[] { 0.05f, 0.06f, 0.09f, 1f };
    [JsonPropertyName("draws")] public List<Draw> Draws { get; set; } = new();
}

public sealed class Draw
{
    [JsonPropertyName("mesh")] public string Mesh { get; set; } = string.Empty;
    [JsonPropertyName("vertex_count")] public int VertexCount { get; set; }
    [JsonPropertyName("offset")] public int Offset { get; set; }
}

public sealed class Camera
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("near")] public float Near { get; set; } = 0.1f;
    [JsonPropertyName("far")] public float Far { get; set; } = 100f;
}

public sealed class MeshRef
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
    [JsonPropertyName("kind")] public string Kind { get; set; } = "triangle";
}

public sealed class DirectionalLight
{
    [JsonPropertyName("direction")] public float[] Direction { get; set; } = new float[] { 0, -1, 0 };
    [JsonPropertyName("color")] public float[] Color { get; set; } = new float[] { 1, 1, 1 };
    [JsonPropertyName("intensity")] public float Intensity { get; set; } = 1.0f;
}
