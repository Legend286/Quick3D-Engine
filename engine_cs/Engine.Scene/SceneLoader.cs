// SPDX-License-Identifier: MIT
// Scene loader: reads Content/scenes/*.scene.json and resolves paths relative
// to the project descriptor's content root.

using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Engine.Scene;

public sealed class SceneLoader
{
    private readonly string _contentRoot;
    private readonly Dictionary<string, Scene> _cache = new();

    public SceneLoader(string contentRoot)
    {
        _contentRoot = contentRoot;
    }

    public Scene Load(string sceneName)
    {
        if (_cache.TryGetValue(sceneName, out var hit)) return hit;
        string path = Path.Combine(_contentRoot, "scenes", sceneName + ".scene.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Scene not found: {path}", path);
        string json = File.ReadAllText(path);
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var scene = JsonSerializer.Deserialize<Scene>(json, opts)
            ?? throw new InvalidDataException($"Empty scene: {path}");
        _cache[sceneName] = scene;
        return scene;
    }

    public string ResolveMeshSource(Scene scene, string meshName)
    {
        foreach (var m in scene.Meshes)
        {
            if (m.Name == meshName)
            {
                if (string.IsNullOrWhiteSpace(m.Source)) return string.Empty;
                return Path.Combine(_contentRoot, m.Source);
            }
        }
        return string.Empty;
    }
}
