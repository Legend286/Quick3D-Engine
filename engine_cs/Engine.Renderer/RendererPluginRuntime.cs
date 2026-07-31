// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using Engine.Plugins;
using Engine.RenderGraph;

namespace Engine.Renderer;

internal sealed class RendererPluginRuntime :
    IEnginePluginHost,
    IDisposable
{
    private sealed class PluginContext :
        AssemblyLoadContext
    {
        private readonly Assembly _gameAssembly;

        public PluginContext(Assembly gameAssembly)
            : base(isCollectible: true)
        {
            _gameAssembly = gameAssembly;
        }

        protected override Assembly? Load(
            AssemblyName assemblyName)
        {
            if (assemblyName.Name == "Engine.Game")
                return _gameAssembly;
            if (assemblyName.Name == "Engine.Plugins")
                return typeof(IEnginePlugin).Assembly;
            if (assemblyName.Name == "Engine.RHI")
                return typeof(Engine.RHI.RhiDevice).Assembly;
            if (assemblyName.Name == "Engine.RenderGraph")
                return typeof(
                    Engine.RenderGraph.RenderPass).Assembly;
            if (assemblyName.Name == "Engine.Scene")
                return typeof(
                    Engine.Scene.SceneGraph).Assembly;
            if (assemblyName.Name == "Engine.Assets")
                return typeof(
                    Engine.Assets.AssetRegistry).Assembly;
            if (assemblyName.Name == "Engine.Renderer")
                return typeof(
                    Engine.Renderer.Renderer).Assembly;
            return null;
        }
    }

    private sealed record LoadedPlugin(
        PluginContext Context,
        IEnginePlugin Instance);

    private static readonly JsonSerializerOptions JsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = true,
            Converters =
            {
                new JsonStringEnumConverter()
            }
        };
    private readonly Dictionary<string, LoadedPlugin>
        _loaded = new(StringComparer.Ordinal);

    public string EngineRoot { get; }
    public string ProjectRoot { get; private set; } = "";

    public RendererPluginRuntime()
    {
        EngineRoot = LocateEngineRoot();
    }

    public void SetProjectRoot(string contentRoot)
    {
        ProjectRoot =
            Directory.GetParent(contentRoot)
                ?.FullName ?? "";
    }

    public IRendererPlanPlugin? LoadClustered()
        => Load("core.renderer.clustered")
            as IRendererPlanPlugin;

    public IRendererPlanPlugin? LoadPathTracing()
        => Load("core.renderer.path-tracing")
            as IRendererPlanPlugin;

    public IEnginePlugin? Load(string pluginId)
    {
        if (_loaded.TryGetValue(
                pluginId,
                out LoadedPlugin? loaded))
        {
            return loaded.Instance;
        }

        string? manifestPath =
            FindManifest(pluginId);
        if (manifestPath == null)
            return null;

        try
        {
            EnginePluginManifest? manifest =
                JsonSerializer.Deserialize<
                    EnginePluginManifest>(
                    File.ReadAllText(manifestPath),
                    JsonOptions);
            if (manifest == null ||
                string.IsNullOrWhiteSpace(
                    manifest.Assembly) ||
                string.IsNullOrWhiteSpace(
                    manifest.EntryPoint))
            {
                return null;
            }

            string directory =
                Path.GetDirectoryName(manifestPath)!;
            string? assemblyPath =
                ResolveAssemblyPath(
                    directory,
                    manifest.Assembly);
            if (assemblyPath == null)
                return null;

            var context = new PluginContext(
                typeof(Renderer).Assembly);
            using var assemblyStream =
                new MemoryStream(
                    File.ReadAllBytes(assemblyPath));
            Assembly assembly =
                context.LoadFromStream(
                    assemblyStream);
            Type? entryPoint =
                assembly.GetType(
                    manifest.EntryPoint);
            if (entryPoint == null ||
                Activator.CreateInstance(entryPoint)
                    is not IEnginePlugin instance)
            {
                context.Unload();
                return null;
            }

            instance.Initialize(this);
            _loaded[pluginId] =
                new LoadedPlugin(
                    context,
                    instance);
            return instance;
        }
        catch (Exception exception)
        {
            Engine.CBindings.Log.Error(
                $"[Plugins] Failed to load {pluginId}: {exception}",
                "Renderer");
            return null;
        }
    }

    public void Unload(string pluginId)
    {
        if (!_loaded.Remove(
                pluginId,
                out LoadedPlugin? loaded))
        {
            return;
        }

        loaded.Instance.Shutdown();
        loaded.Instance.Dispose();
        loaded.Context.Unload();
    }

    public void InvalidatePluginShaders(
        string pluginId)
    {
    }

    /// <inheritdoc />
    /// <remarks>Forwards to <see cref="Engine.Renderer.Renderer.TryGetActiveCameraData"/>
    /// and re-marshals the host-side <c>CameraData</c> struct into the
    /// neutral <see cref="System.Numerics.Vector3"/> + matrix shape the
    /// plugin contract expects. Returns false when the host has no
    /// active camera so plugin passes can fall back to identity.

    public bool TryGetActiveCameraData(
        uint width, uint height,
        out System.Numerics.Vector3 cameraPosition,
        out System.Numerics.Matrix4x4 viewProjection,
        out System.Numerics.Matrix4x4 inverseViewProjection)
    {
        cameraPosition = System.Numerics.Vector3.Zero;
        viewProjection = System.Numerics.Matrix4x4.Identity;
        inverseViewProjection = System.Numerics.Matrix4x4.Identity;

        var renderer = Renderer.ActiveInstance;
        if (renderer == null)
        {
            return false;
        }

        if (!renderer.TryGetActiveCameraData(
                width, height,
                out _, out _,
                out CameraData cameraData))
        {
            return false;
        }
        if (cameraData.CameraPosition is { } streamCamPos)
        {
            cameraPosition = new System.Numerics.Vector3(
                streamCamPos.X,
                streamCamPos.Y,
                streamCamPos.Z);
        }
        else
        {
            cameraPosition = System.Numerics.Vector3.Zero;
        }
        viewProjection = cameraData.ViewProj;
        inverseViewProjection = cameraData.InvViewProj;
        return true;
    }

    private string? FindManifest(string pluginId)
    {
        string root =
            Path.Combine(EngineRoot, "Plugins");
        if (!Directory.Exists(root))
            return null;

        foreach (string path in
                 Directory.EnumerateFiles(
                     root,
                     "plugin.json",
                     SearchOption.AllDirectories))
        {
            try
            {
                using JsonDocument document =
                    JsonDocument.Parse(
                        File.ReadAllText(path));
                if (document.RootElement
                        .TryGetProperty(
                            "id",
                            out JsonElement id) &&
                    id.GetString() == pluginId)
                {
                    return path;
                }
            }
            catch
            {
            }
        }
        return null;
    }

    private static string? ResolveAssemblyPath(
        string directory,
        string assembly)
    {
        string[] candidates =
        [
            Path.Combine(directory, assembly),
            Path.Combine(
                directory,
                "bin",
                "Debug",
                "net8.0",
                assembly),
            Path.Combine(
                directory,
                "bin",
                "Release",
                "net8.0",
                assembly)
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string LocateEngineRoot()
    {
        foreach (string start in
                 new[]
                 {
                     Environment.CurrentDirectory,
                     AppContext.BaseDirectory
                 })
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

    public void Dispose()
    {
        foreach (string pluginId in
                 _loaded.Keys.ToArray())
        {
            Unload(pluginId);
        }
    }
}
