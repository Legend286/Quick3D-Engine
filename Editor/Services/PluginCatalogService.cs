// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Engine.CBindings;
using Engine.Plugins;

namespace Engine.Editor.Services;

/// <summary>Represents one engine plugin in editor UI.</summary>
public sealed partial class PluginEntryViewModel :
    ObservableObject
{
    private readonly PluginCatalogService _catalog;

    /// <summary>Gets the plugin manifest.</summary>
    public EnginePluginManifest Manifest { get; }

    /// <summary>Gets the plugin manifest directory.</summary>
    public string DirectoryPath { get; }

    /// <summary>Gets whether the enable control can be changed.</summary>
    public bool IsToggleable => !Manifest.Required;

    /// <summary>Gets the plugin identifier.</summary>
    public string Id => Manifest.Id;

    /// <summary>Gets the plugin display name.</summary>
    public string Name => Manifest.Name;

    /// <summary>Gets the plugin description.</summary>
    public string Description => Manifest.Description;

    /// <summary>Gets a user-facing plugin state label.</summary>
    public string Status => Manifest.Required
        ? "Required"
        : IsEnabled
            ? "Enabled"
            : "Disabled";

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (Manifest.Required && !value)
        {
            IsEnabled = true;
            return;
        }
        _catalog.SetEnabled(this, value);
        OnPropertyChanged(nameof(Status));
    }

    internal PluginEntryViewModel(
        PluginCatalogService catalog,
        EnginePluginManifest manifest,
        string directoryPath,
        bool enabled)
    {
        _catalog = catalog;
        Manifest = manifest;
        DirectoryPath = directoryPath;
        _isEnabled = manifest.Required || enabled;
    }

    internal void SetEnabledSilently(bool enabled)
    {
        _isEnabled = Manifest.Required || enabled;
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(Status));
    }
}

/// <summary>Discovers, configures, watches, and reloads managed engine plugins.</summary>
public sealed class PluginCatalogService :
    IEnginePluginHost,
    IDisposable
{
    private sealed class RuntimePlugin
    {
        public required PluginLoadContext Context;
        public required IEnginePlugin Instance;
    }

    private sealed class PluginLoadContext :
        AssemblyLoadContext
    {
        public PluginLoadContext()
            : base(isCollectible: true)
        {
        }

        protected override Assembly? Load(
            AssemblyName assemblyName)
        {
            if (assemblyName.Name is
                "Engine.Plugins" or
                "Engine.RHI" or
                "Engine.RenderGraph" or
                "Engine.Scene" or
                "Engine.Assets" or
                "Engine.CBindings")
            {
                return Default.LoadFromAssemblyName(
                    assemblyName);
            }
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters =
            {
                new System.Text.Json.Serialization
                    .JsonStringEnumConverter()
            }
        };
    private readonly Dictionary<string, RuntimePlugin>
        _runtime = new(StringComparer.Ordinal);
    private readonly List<FileSystemWatcher> _watchers =
        new();
    private readonly Dictionary<string, Timer> _debounce =
        new(StringComparer.Ordinal);
    private bool _writingConfiguration;
    private bool _disposed;

    /// <summary>Gets the process-wide plugin catalog.</summary>
    public static PluginCatalogService Shared { get; } =
        new(App.ProjectRoot);

    /// <summary>Gets discovered plugins.</summary>
    public ObservableCollection<PluginEntryViewModel>
        Plugins { get; } = new();

    /// <inheritdoc />
    public string EngineRoot { get; }

    /// <inheritdoc />
    public string ProjectRoot { get; }

    /// <summary>Occurs when plugin shader pipelines must be recreated.</summary>
    public event Action<string>? ShaderReloadRequested;

    /// <summary>Occurs when a rebuilt managed plugin assembly is available.</summary>
    public event Action<string>? CodeReloadRequested;

    /// <summary>Occurs after plugin availability changes.</summary>
    public event Action? AvailabilityChanged;

    private PluginCatalogService(string projectRoot)
    {
        ProjectRoot = projectRoot;
        EngineRoot = LocateEngineRoot();
        Discover();
    }

    /// <summary>Gets whether a plugin is enabled for the active project.</summary>
    public bool IsEnabled(string pluginId)
        => Plugins.FirstOrDefault(
            plugin => plugin.Id == pluginId)
            ?.IsEnabled == true;

    /// <summary>Enables an optional plugin by identifier.</summary>
    public bool Enable(string pluginId)
    {
        PluginEntryViewModel? plugin =
            Plugins.FirstOrDefault(
                item => item.Id == pluginId);
        if (plugin == null)
            return false;

        plugin.IsEnabled = true;
        return plugin.IsEnabled;
    }

    internal void SetEnabled(
        PluginEntryViewModel plugin,
        bool enabled)
    {
        if (_disposed ||
            _writingConfiguration ||
            (plugin.Manifest.Required && !enabled))
        {
            return;
        }

        if (enabled)
        {
            LoadPlugin(plugin);
            if (plugin.Manifest.Kind ==
                    EnginePluginKind.Renderer &&
                ResolveAssemblyPath(plugin) == null)
            {
                _ = BuildPluginAsync(plugin);
            }
        }
        else
            UnloadPlugin(plugin.Id);

        WriteConfiguration();
        AvailabilityChanged?.Invoke();
    }

    /// <inheritdoc />
    public void InvalidatePluginShaders(
        string pluginId)
    {
        ShaderReloadRequested?.Invoke(pluginId);
    }

    private void Discover()
    {
        string pluginsRoot =
            Path.Combine(EngineRoot, "Plugins");
        if (!Directory.Exists(pluginsRoot))
            return;

        HashSet<string> enabled = ReadEnabledIds();
        foreach (string manifestPath in
                 Directory.EnumerateFiles(
                     pluginsRoot,
                     "plugin.json",
                     SearchOption.AllDirectories))
        {
            try
            {
                EnginePluginManifest? manifest =
                    JsonSerializer.Deserialize<
                        EnginePluginManifest>(
                        File.ReadAllText(manifestPath),
                        JsonOptions);
                if (manifest == null ||
                    string.IsNullOrWhiteSpace(
                        manifest.Id))
                {
                    continue;
                }

                var entry = new PluginEntryViewModel(
                    this,
                    manifest,
                    Path.GetDirectoryName(manifestPath)!,
                    enabled.Contains(manifest.Id));
                Plugins.Add(entry);
                if (entry.IsEnabled)
                    LoadPlugin(entry);
                WatchPlugin(entry);
            }
            catch (Exception exception)
            {
                Log.Error(
                    $"[Plugins] Failed to read '{manifestPath}': {exception.Message}",
                    "Editor");
            }
        }
    }

    private HashSet<string> ReadEnabledIds()
    {
        var enabled = new HashSet<string>(
            StringComparer.Ordinal);
        string path = GetConfigurationPath();
        if (!File.Exists(path))
            return enabled;

        try
        {
            using JsonDocument document =
                JsonDocument.Parse(
                    File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty(
                    "enabled",
                    out JsonElement entries))
            {
                return enabled;
            }
            foreach (JsonElement entry in
                     entries.EnumerateArray())
            {
                if (entry.TryGetProperty(
                        "id",
                        out JsonElement id))
                {
                    string? value = id.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        enabled.Add(value);
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error(
                $"[Plugins] Failed to read addons.json: {exception.Message}",
                "Editor");
        }
        return enabled;
    }

    private void WriteConfiguration()
    {
        string path = GetConfigurationPath();
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!);
        JsonObject root;
        try
        {
            root = File.Exists(path)
                ? JsonNode.Parse(
                    File.ReadAllText(path))
                    ?.AsObject() ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        root["version"] = 1;
        var enabled = new JsonArray();
        foreach (PluginEntryViewModel plugin in
                 Plugins.Where(
                     item =>
                         item.IsEnabled &&
                         !item.Manifest.Required))
        {
            enabled.Add(
                new JsonObject
                {
                    ["id"] = plugin.Id,
                    ["version"] =
                        plugin.Manifest.PluginVersion
                });
        }
        root["enabled"] = enabled;

        string temporaryPath = path + ".tmp";
        _writingConfiguration = true;
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(
                    root.ToJsonString(JsonOptions));
                writer.Flush();
                stream.Flush(true);
            }
            File.Move(
                temporaryPath,
                path,
                true);
        }
        finally
        {
            _writingConfiguration = false;
        }
    }

    private void WatchPlugin(
        PluginEntryViewModel plugin)
    {
        var directories =
            new HashSet<string>(
                StringComparer.Ordinal)
            {
                Path.GetFullPath(
                    plugin.DirectoryPath)
            };
        foreach (string shaderFile in
                 plugin.Manifest.ShaderFiles)
        {
            string fullPath = Path.GetFullPath(
                Path.Combine(
                    plugin.DirectoryPath,
                    shaderFile));
            string? directory =
                Path.GetDirectoryName(fullPath);
            if (directory != null &&
                Directory.Exists(directory))
            {
                directories.Add(
                    Path.GetFullPath(directory));
            }
        }
        foreach (string directory in directories)
            WatchDirectory(plugin, directory);
    }

    private void WatchDirectory(
        PluginEntryViewModel plugin,
        string directory)
    {
        var watcher = new FileSystemWatcher(directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter =
                NotifyFilters.LastWrite |
                NotifyFilters.FileName
        };
        watcher.Changed += (_, args) =>
            HandleFileChange(plugin, args.FullPath);
        watcher.Created += (_, args) =>
            HandleFileChange(plugin, args.FullPath);
        watcher.Renamed += (_, args) =>
            HandleFileChange(plugin, args.FullPath);
        watcher.EnableRaisingEvents = true;
        _watchers.Add(watcher);
    }

    private void HandleFileChange(
        PluginEntryViewModel plugin,
        string path)
    {
        string extension =
            Path.GetExtension(path)
                .ToLowerInvariant();
        if (extension is ".slang" or
            ".hlsl" or
            ".glsl" or
            ".metal")
        {
            if (!plugin.Manifest.ShaderFiles.Any(
                    shader =>
                        string.Equals(
                            Path.GetFullPath(
                                Path.Combine(
                                    plugin.DirectoryPath,
                                    shader)),
                            Path.GetFullPath(path),
                            StringComparison.Ordinal)))
            {
                return;
            }
            Debounce(
                $"shader:{plugin.Id}",
                () =>
                {
                    if (plugin.IsEnabled)
                        ShaderReloadRequested?.Invoke(
                            plugin.Id);
                });
            return;
        }
        if (extension is ".cs" or ".csproj")
        {
            string relativePath =
                Path.GetRelativePath(
                    plugin.DirectoryPath,
                    path);
            if (relativePath.StartsWith(
                    "obj" +
                    Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) ||
                relativePath.StartsWith(
                    "bin" +
                    Path.DirectorySeparatorChar,
                    StringComparison.Ordinal))
            {
                return;
            }
            Debounce(
                $"source:{plugin.Id}",
                () => _ = BuildPluginAsync(plugin),
                600);
            return;
        }
        if (extension == ".dll")
        {
            if (!string.Equals(
                    Path.GetFileName(path),
                    plugin.Manifest.Assembly,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            Debounce(
                $"binary:{plugin.Id}",
                () =>
                {
                    if (!plugin.IsEnabled)
                        return;
                    UnloadPlugin(plugin.Id);
                    LoadPlugin(plugin);
                    CodeReloadRequested?.Invoke(
                        plugin.Id);
                });
        }
    }

    private void Debounce(
        string key,
        Action callback,
        int delayMilliseconds = 180)
    {
        lock (_debounce)
        {
            if (_debounce.Remove(
                    key,
                    out Timer? existing))
            {
                existing.Dispose();
            }
            _debounce[key] = new Timer(
                _ =>
                {
                    lock (_debounce)
                    {
                        if (_debounce.Remove(
                                key,
                                out Timer? timer))
                        {
                            timer.Dispose();
                        }
                    }
                    callback();
                },
                null,
                delayMilliseconds,
                Timeout.Infinite);
        }
    }

    private async Task BuildPluginAsync(
        PluginEntryViewModel plugin)
    {
        string? project = Directory
            .EnumerateFiles(
                plugin.DirectoryPath,
                "*.csproj",
                SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        if (project == null)
            return;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "dotnet",
                    WorkingDirectory =
                        plugin.DirectoryPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            process.StartInfo.ArgumentList.Add("build");
            process.StartInfo.ArgumentList.Add(project);
            process.StartInfo.ArgumentList.Add(
                "--nologo");
            process.Start();
            string output =
                await process.StandardOutput
                    .ReadToEndAsync();
            string error =
                await process.StandardError
                    .ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                Log.Error(
                    $"[Plugins] Build failed for {plugin.Name}: {error}{output}",
                    "Build");
            }
        }
        catch (Exception exception)
        {
            Log.Error(
                $"[Plugins] Build failed for {plugin.Name}: {exception.Message}",
                "Build");
        }
    }

    private void LoadPlugin(
        PluginEntryViewModel plugin)
    {
        if (plugin.Manifest.Kind ==
            EnginePluginKind.Renderer)
        {
            return;
        }
        if (_runtime.ContainsKey(plugin.Id) ||
            string.IsNullOrWhiteSpace(
                plugin.Manifest.Assembly) ||
            string.IsNullOrWhiteSpace(
                plugin.Manifest.EntryPoint))
        {
            return;
        }

        string? assemblyPath =
            ResolveAssemblyPath(plugin);
        if (assemblyPath == null)
            return;

        try
        {
            var context = new PluginLoadContext();
            byte[] assemblyBytes =
                File.ReadAllBytes(assemblyPath);
            using var stream =
                new MemoryStream(assemblyBytes);
            Assembly assembly =
                context.LoadFromStream(stream);
            Type? type = assembly.GetType(
                plugin.Manifest.EntryPoint,
                throwOnError: false);
            if (type == null ||
                Activator.CreateInstance(type) is not
                    IEnginePlugin instance)
            {
                context.Unload();
                return;
            }
            instance.Initialize(this);
            _runtime[plugin.Id] =
                new RuntimePlugin
                {
                    Context = context,
                    Instance = instance
                };
        }
        catch (Exception exception)
        {
            Log.Error(
                $"[Plugins] Failed to load {plugin.Name}: {exception.Message}",
                "Editor");
        }
    }

    private string? ResolveAssemblyPath(
        PluginEntryViewModel plugin)
    {
        string assemblyName =
            plugin.Manifest.Assembly!;
        string[] candidates =
        [
            Path.Combine(
                plugin.DirectoryPath,
                assemblyName),
            Path.Combine(
                plugin.DirectoryPath,
                "bin",
                "Debug",
                "net8.0",
                assemblyName),
            Path.Combine(
                plugin.DirectoryPath,
                "bin",
                "Release",
                "net8.0",
                assemblyName)
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private void UnloadPlugin(string pluginId)
    {
        if (!_runtime.Remove(
                pluginId,
                out RuntimePlugin? runtime))
        {
            return;
        }

        runtime.Instance.Shutdown();
        runtime.Instance.Dispose();
        runtime.Context.Unload();
    }

    private string GetConfigurationPath()
        => Path.Combine(
            ProjectRoot,
            ".eeproj",
            "addons.json");

    private static string LocateEngineRoot()
    {
        string[] starts =
        [
            Environment.CurrentDirectory,
            AppContext.BaseDirectory
        ];
        foreach (string start in starts)
        {
            DirectoryInfo? directory =
                new DirectoryInfo(start);
            for (int depth = 0;
                 directory != null && depth < 10;
                 ++depth,
                 directory = directory.Parent)
            {
                if (Directory.Exists(
                        Path.Combine(
                            directory.FullName,
                            "Plugins")))
                {
                    return directory.FullName;
                }
            }
        }
        return AppContext.BaseDirectory;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (string pluginId in
                 _runtime.Keys.ToArray())
        {
            UnloadPlugin(pluginId);
        }
        foreach (FileSystemWatcher watcher in
                 _watchers)
        {
            watcher.Dispose();
        }
        lock (_debounce)
        {
            foreach (Timer timer in
                     _debounce.Values)
            {
                timer.Dispose();
            }
            _debounce.Clear();
        }
    }
}
