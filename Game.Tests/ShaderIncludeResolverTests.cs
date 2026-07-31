// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Engine.Plugins;
using Engine.RenderGraph.Shaders;
using Xunit;

namespace Engine.Game.Tests;

public sealed class ShaderIncludeResolverTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _contentRoot;
    private readonly string _engineShaders;

    public ShaderIncludeResolverTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            "Engine.ShaderIncludeResolver.Tests",
            Guid.NewGuid().ToString("N"));
        _contentRoot = Path.Combine(_tempRoot, "Content");
        _engineShaders = Path.Combine(_contentRoot, "shaders");
        Directory.CreateDirectory(_engineShaders);
    }

    public void Dispose()
    {
        TryDelete(_tempRoot);
    }

    [Fact]
    public void Resolve_NoPlugins_ReturnsOnlyEngineDefault()
    {
        IReadOnlyList<string> result = ShaderIncludeResolver.Resolve(
            _contentRoot,
            Array.Empty<(EnginePluginManifest, string)>());

        Assert.Single(result);
        Assert.Equal(_engineShaders, result[0]);
    }

    [Fact]
    public void Resolve_SinglePluginSingleInclude_PrependsToEngineDefault()
    {
        string pluginDir = Path.Combine(_tempRoot, "plugins", "P1");
        string shaderDir = Path.Combine(pluginDir, "shaders");
        Directory.CreateDirectory(shaderDir);

        EnginePluginManifest manifest = MakePlugin(
            "shaders/",
            Path.Combine(pluginDir, "plugin.json"));

        IReadOnlyList<string> result = ShaderIncludeResolver.Resolve(
            _contentRoot,
            new[] { (manifest, Path.Combine(pluginDir, "plugin.json")) });

        Assert.Equal(2, result.Count);
        Assert.Equal(shaderDir, result[0]);
        Assert.Equal(_engineShaders, result[1]);
    }

    [Fact]
    public void Resolve_MultiplePlugins_PreservesEnumerationOrder()
    {
        (string plugin, string shaders)[] entries =
        {
            MakePluginEntry("P1"),
            MakePluginEntry("P2"),
            MakePluginEntry("P3"),
        };

        EnginePluginManifest m1 = MakePlugin(
            "shaders/",
            Path.Combine(entries[0].plugin, "plugin.json"));
        EnginePluginManifest m2 = MakePlugin(
            "shaders/",
            Path.Combine(entries[1].plugin, "plugin.json"));
        EnginePluginManifest m3 = MakePlugin(
            "shaders/",
            Path.Combine(entries[2].plugin, "plugin.json"));

        IReadOnlyList<string> result = ShaderIncludeResolver.Resolve(
            _contentRoot,
            new[]
            {
                (m1, Path.Combine(entries[0].plugin, "plugin.json")),
                (m2, Path.Combine(entries[1].plugin, "plugin.json")),
                (m3, Path.Combine(entries[2].plugin, "plugin.json")),
            });

        Assert.Equal(
            new[] { entries[0].shaders, entries[1].shaders, entries[2].shaders, _engineShaders },
            result.ToArray());
    }

    [Fact]
    public void Resolve_DuplicatePathAcrossPlugins_DedupesPreservingFirst()
    {
        string sharedShaders = Path.Combine(_tempRoot, "sharedShaders");
        Directory.CreateDirectory(sharedShaders);

        string p1 = Path.Combine(_tempRoot, "plugins", "P1");
        string p2 = Path.Combine(_tempRoot, "plugins", "P2");
        Directory.CreateDirectory(p1);
        Directory.CreateDirectory(p2);

        EnginePluginManifest m1 = MakePlugin(
            sharedShaders,
            Path.Combine(p1, "plugin.json"));
        // Absolute path intentionally identical to m1's contribution.
        EnginePluginManifest m2 = MakePlugin(
            sharedShaders,
            Path.Combine(p2, "plugin.json"));

        IReadOnlyList<string> result = ShaderIncludeResolver.Resolve(
            _contentRoot,
            new[]
            {
                (m1, Path.Combine(p1, "plugin.json")),
                (m2, Path.Combine(p2, "plugin.json")),
            });

        Assert.Equal(2, result.Count);
        Assert.Equal(sharedShaders, result[0]);
        Assert.Equal(_engineShaders, result[1]);
    }

    [Fact]
    public void Resolve_MissingIncludeDir_SilentlySkipped()
    {
        string pluginDir = Path.Combine(_tempRoot, "plugins", "StalePlugin");
        Directory.CreateDirectory(pluginDir);
        // Intentionally do NOT create <pluginDir>/shaders — drives the
        // sentinel that Directory.Exists returns false.

        EnginePluginManifest manifest = MakePlugin(
            "shaders/",
            Path.Combine(pluginDir, "plugin.json"));

        IReadOnlyList<string> result = ShaderIncludeResolver.Resolve(
            _contentRoot,
            new[] { (manifest, Path.Combine(pluginDir, "plugin.json")) });

        Assert.Single(result);
        Assert.Equal(_engineShaders, result[0]);
    }

    [Fact]
    public void Resolve_AbsolutePathInManifest_UsedVerbatim()
    {
        string absoluteShaderDir = Path.Combine(_tempRoot, "extraShaders");
        Directory.CreateDirectory(absoluteShaderDir);

        EnginePluginManifest manifest = MakePlugin(
            absoluteShaderDir,
            Path.Combine(_tempRoot, "plugins", "AbsolutePath", "plugin.json"));

        IReadOnlyList<string> result = ShaderIncludeResolver.Resolve(
            _contentRoot,
            new[]
            {
                (manifest, Path.Combine(_tempRoot, "plugins", "AbsolutePath", "plugin.json")),
            });

        Assert.Equal(2, result.Count);
        Assert.Equal(absoluteShaderDir, result[0]);
        Assert.Equal(_engineShaders, result[1]);
    }

    [Fact]
    public void Resolve_NullContentRoot_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ShaderIncludeResolver.Resolve(
                null!,
                Array.Empty<(EnginePluginManifest, string)>()));
    }

    [Fact]
    public void Resolve_NullEnabledPlugins_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ShaderIncludeResolver.Resolve(_contentRoot, null!));
    }

    [Fact]
    public void Resolve_PluginWithNullShaderIncludes_TreatedAsEmpty()
    {
        string pluginDir = Path.Combine(_tempRoot, "plugins", "NullIncludes");
        Directory.CreateDirectory(pluginDir);

        EnginePluginManifest manifest = new()
        {
            Id = "core.tests.null-includes",
            ShaderIncludes = null!,
        };

        IReadOnlyList<string> result = ShaderIncludeResolver.Resolve(
            _contentRoot,
            new[] { (manifest, Path.Combine(pluginDir, "plugin.json")) });

        Assert.Single(result);
        Assert.Equal(_engineShaders, result[0]);
    }

    private (string Plugin, string Shaders) MakePluginEntry(string name)
    {
        string plugin = Path.Combine(_tempRoot, "plugins", name);
        string shaders = Path.Combine(plugin, "shaders");
        Directory.CreateDirectory(shaders);
        return (plugin, shaders);
    }

    private static EnginePluginManifest MakePlugin(string include, string manifestPath)
    {
        ArgumentNullException.ThrowIfNull(manifestPath);
        return new EnginePluginManifest
        {
            ShaderIncludes = new List<string> { include },
        };
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; OS warm-restart will sweep temp.
        }
    }
}
