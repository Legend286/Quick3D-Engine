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

    /// <summary>Gets or sets shader include directories owned by the plugin.</summary>
    /// <remarks>
    /// Paths are relative to the plugin's manifest directory. They are
    /// resolved by <see cref="Engine.Renderer.ShaderIncludeResolver"/> in
    /// manifest-discovery order, with the engine's
    /// <c>ContentRoot/shaders</c> appended last as the lowest-priority
    /// fallback. Plugins use these to ship <c>*.slang</c> include files that
    /// compose with the engine's host shaders (e.g. <c>pbr.slang</c>) via
    /// Slang <c>#include</c> directives without forking the host source.
    /// </remarks>
    [JsonPropertyName("shader_includes")]
    public List<string> ShaderIncludes { get; set; } = new();

    /// <summary>Gets or sets the Slang preprocessor feature flags owned by
    /// the plugin.</summary>
    /// <remarks>
    /// Each entry is the name of a preprocessor macro that the plugin's
    /// shader files rely on being defined (e.g. <c>DDGI_PLUGIN</c>). When
    /// this plugin is enabled, <see cref="Engine.Renderer.ShaderCompileCache"/>
    /// expands these into Slang <c>-D NAME=1</c> argv tokens threaded
    /// through <see cref="Engine.RHI.RhiShader.FromSource"/>'s
    /// <c>cliArgs</c> parameter, so host shaders can gate plugin-shader
    /// overrides with <c>#ifdef NAME</c>. Duplicates across plugins are
    /// collapsed deterministically (sorted Ordinal compare).
    /// </remarks>
    [JsonPropertyName("shader_features")]
    public List<string> ShaderFeatures { get; set; } = new();
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

/// <summary>Provides stable host services to an editor plugin.</summary>
public interface IEditorPluginHost : IEnginePluginHost
{
    /// <summary>Registers a menu action in the Editor UI.</summary>
    void RegisterMenuAction(string pluginId, string menuPath, string itemName, Action onExecute);

    /// <summary>Registers an ImGui draw callback over the viewport.</summary>
    void RegisterImGuiOverlay(string pluginId, Action onDraw);

    /// <summary>Registers a tool panel (Avalonia control) in the Editor UI.</summary>
    void RegisterToolPanel(string pluginId, string title, object avaloniaControl);
}

/// <summary>Defines a plugin specifically designed to extend the Editor.</summary>
public interface IEditorPlugin : IEnginePlugin
{
    /// <summary>Initializes the editor plugin against editor host services.</summary>
    void InitializeEditor(IEditorPluginHost host);
}
