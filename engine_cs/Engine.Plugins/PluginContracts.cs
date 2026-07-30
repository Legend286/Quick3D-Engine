// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Engine.Plugins;

/// <summary>Identifies the primary capability supplied by a plugin.</summary>
public enum EnginePluginKind
{
    /// <summary>Supplies a viewport or runtime renderer.</summary>
    Renderer,

    /// <summary>Supplies editor tools or panels.</summary>
    Editor,

    /// <summary>Supplies runtime systems.</summary>
    Runtime,

    /// <summary>Supplies asset import or cooking stages.</summary>
    AssetPipeline
}

/// <summary>Describes an engine-owned managed plugin.</summary>
public sealed class EnginePluginManifest
{
    /// <summary>Gets or sets the manifest schema version.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>Gets or sets the stable reverse-DNS plugin identifier.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Gets or sets the user-facing plugin name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the plugin description.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    /// <summary>Gets or sets the plugin semantic version.</summary>
    [JsonPropertyName("plugin_version")]
    public string PluginVersion { get; set; } = "1.0.0";

    /// <summary>Gets or sets the primary plugin capability.</summary>
    [JsonPropertyName("kind")]
    public EnginePluginKind Kind { get; set; }

    /// <summary>Gets or sets whether projects may disable the plugin.</summary>
    [JsonPropertyName("required")]
    public bool Required { get; set; }

    /// <summary>Gets or sets the managed assembly path relative to the manifest.</summary>
    [JsonPropertyName("assembly")]
    public string? Assembly { get; set; }

    /// <summary>Gets or sets the plugin entry-point type name.</summary>
    [JsonPropertyName("entry_point")]
    public string? EntryPoint { get; set; }

    /// <summary>Gets or sets shader files owned by the plugin.</summary>
    [JsonPropertyName("shader_files")]
    public List<string> ShaderFiles { get; set; } = new();
}

/// <summary>Provides stable host services to a managed engine plugin.</summary>
public interface IEnginePluginHost
{
    /// <summary>Gets the engine installation or source root.</summary>
    string EngineRoot { get; }

    /// <summary>Gets the active project root.</summary>
    string ProjectRoot { get; }

    /// <summary>Requests recreation of pipelines owned by a plugin.</summary>
    void InvalidatePluginShaders(string pluginId);
}

/// <summary>Defines the lifecycle of a hot-reloadable managed plugin.</summary>
public interface IEnginePlugin : IDisposable
{
    /// <summary>Gets the stable plugin identifier.</summary>
    string Id { get; }

    /// <summary>Initializes the plugin against host-owned services.</summary>
    void Initialize(IEnginePluginHost host);

    /// <summary>Releases registrations before the load context unloads.</summary>
    void Shutdown();
}
