// SPDX-License-Identifier: MIT
// Plain data types describing a scene loaded from a Content/scenes/*.scene.json.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Engine.Scene;

public sealed class SceneGraph
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("passes")] public List<ScenePass> Passes { get; set; } = new();
    [JsonPropertyName("cameras")] public List<Camera> Cameras { get; set; } = new();
    [JsonPropertyName("meshes")] public List<MeshRef> Meshes { get; set; } = new();
    [JsonPropertyName("models")] public List<ModelRef> Models { get; set; } = new();
    [JsonPropertyName("lights")] public List<LightNode> Lights { get; set; } = new();
    [JsonPropertyName("procedural_demo")] public ProceduralDemoDefinition? ProceduralDemo { get; set; }
}

/// <summary>
/// Parameters for the built-in procedural renderer stress scene.
/// </summary>
public sealed class ProceduralDemoDefinition
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("point_light_count")] public int PointLightCount { get; set; } = 28;
    [JsonPropertyName("spot_light_count")] public int SpotLightCount { get; set; } = 8;
    [JsonPropertyName("animate_lights")] public bool AnimateLights { get; set; } = true;
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
    [JsonPropertyName("vertices")] public List<Vertex>? Vertices { get; set; }
}

public sealed class Vertex
{
    [JsonPropertyName("pos")] public float[] Pos { get; set; } = new float[3];
    [JsonPropertyName("color")] public float[] Color { get; set; } = new float[3];
}


public sealed class LightNode
{
    [JsonPropertyName("type")] public string Type { get; set; } = "directional"; // "directional", "point", "spot"
    [JsonPropertyName("position")] public float[] Position { get; set; } = new float[] { 0, 0, 0 };
    [JsonPropertyName("direction")] public float[] Direction { get; set; } = new float[] { 0, -1, 0 };
    [JsonPropertyName("color")] public float[] Color { get; set; } = new float[] { 1, 1, 1 };
    [JsonPropertyName("intensity")] public float Intensity { get; set; } = 1.0f;
    [JsonPropertyName("range")] public float Range { get; set; } = 10.0f;
    [JsonPropertyName("inner_cone")] public float InnerCone { get; set; } = 0.8f;
    [JsonPropertyName("outer_cone")] public float OuterCone { get; set; } = 0.7f;
    [JsonPropertyName("source_radius")] public float SourceRadius { get; set; } = 0.05f;
    [JsonPropertyName("sun_radius")] public float SunRadius { get; set; } = 0.00465f;
    [JsonPropertyName("cast_shadows")] public bool CastShadows { get; set; } = true;
}

public sealed class ModelRef
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
    [JsonPropertyName("position")] public float[] Position { get; set; } = new float[] { 0, 0, 0 };
    [JsonPropertyName("rotation")] public float[] Rotation { get; set; } = new float[] { 0, 0, 0, 1 };
    [JsonPropertyName("scale")] public float[] Scale { get; set; } = new float[] { 1, 1, 1 };
    [JsonPropertyName("static_shadow_caster")] public bool StaticShadowCaster { get; set; } = true;
    [JsonPropertyName("part_index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PartIndex { get; set; }
}
