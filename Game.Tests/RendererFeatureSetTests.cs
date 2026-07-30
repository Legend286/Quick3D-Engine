// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using Engine.Plugins;
using Engine.Renderer;
using Xunit;

namespace Engine.Game.Tests;

public sealed class RendererFeatureSetTests
{
    private static (EnginePluginManifest, bool) Entry(EnginePluginManifest m, bool enabled)
        => (m, enabled);

    [Fact]
    public void BuildCliArgs_NullPlugins_ReturnsNull()
    {
        Assert.Null(RendererFeatureSet.BuildCliArgs(null));
    }

    [Fact]
    public void BuildCliArgs_EmptyPlugins_ReturnsNull()
    {
        Assert.Null(RendererFeatureSet.BuildCliArgs(
            Array.Empty<(EnginePluginManifest, bool)>()));
    }

    [Fact]
    public void BuildCliArgs_DisabledPlugin_ContributesZeroTokens()
    {
        EnginePluginManifest m = new() { ShaderFeatures = new() { "DDGI_PLUGIN" } };
        IReadOnlyList<string>? result = RendererFeatureSet.BuildCliArgs(
            new[] { Entry(m, false) });
        Assert.Null(result);
    }

    [Fact]
    public void BuildCliArgs_EnabledPluginSingleFeature_ExpandsToDashDPair()
    {
        EnginePluginManifest m = new() { ShaderFeatures = new() { "DDGI_PLUGIN" } };
        IReadOnlyList<string>? result = RendererFeatureSet.BuildCliArgs(
            new[] { Entry(m, true) });
        Assert.NotNull(result);
        Assert.Equal(new[] { "-D", "DDGI_PLUGIN=1" }, result);
    }

    [Fact]
    public void BuildCliArgs_MultiplePlugins_DedupesAndSortsFeatures()
    {
        EnginePluginManifest m1 = new() { ShaderFeatures = new() { "PATH_TRACING", "B" } };
        EnginePluginManifest m2 = new() { ShaderFeatures = new() { "A", "PATH_TRACING" } };
        IReadOnlyList<string>? result = RendererFeatureSet.BuildCliArgs(
            new[] { Entry(m1, true), Entry(m2, true) });
        Assert.NotNull(result);
        Assert.Equal(
            new[] { "-D", "A=1", "-D", "B=1", "-D", "PATH_TRACING=1" },
            result);
    }

    [Fact]
    public void BuildCliArgs_WhitespaceFeatureEntries_AreSkipped()
    {
        EnginePluginManifest m = new()
        {
            ShaderFeatures = new() { "DDGI_PLUGIN", "  ", "" },
        };
        IReadOnlyList<string>? result = RendererFeatureSet.BuildCliArgs(
            new[] { Entry(m, true) });
        Assert.NotNull(result);
        Assert.Equal(new[] { "-D", "DDGI_PLUGIN=1" }, result);
    }

    [Fact]
    public void BuildCliArgs_PluginWithNullShaderFeatures_TreatedAsEmpty()
    {
        EnginePluginManifest m = new() { ShaderFeatures = null! };
        IReadOnlyList<string>? result = RendererFeatureSet.BuildCliArgs(
            new[] { Entry(m, true) });
        Assert.Null(result);
    }

    [Fact]
    public void BuildCliArgs_MixedEnabledDisabled_OnlyEnabledContributes()
    {
        EnginePluginManifest enabled = new() { ShaderFeatures = new() { "DDGI_PLUGIN" } };
        EnginePluginManifest disabled = new() { ShaderFeatures = new() { "OTHER" } };
        IReadOnlyList<string>? result = RendererFeatureSet.BuildCliArgs(
            new[] { Entry(disabled, false), Entry(enabled, true) });
        Assert.Equal(new[] { "-D", "DDGI_PLUGIN=1" }, result);
    }

    [Fact]
    public void FeatureSetHash_NullOrEmpty_ReturnsZero()
    {
        Assert.Equal(0, RendererFeatureSet.FeatureSetHash(null));
        Assert.Equal(0, RendererFeatureSet.FeatureSetHash(new List<string>()));
    }

    [Fact]
    public void FeatureSetHash_SameSortedInput_IsStableAcrossCalls()
    {
        int first = RendererFeatureSet.FeatureSetHash(new[] { "A", "B", "C" });
        int second = RendererFeatureSet.FeatureSetHash(new[] { "A", "B", "C" });
        Assert.Equal(first, second);
    }

    [Fact]
    public void FeatureSetHash_DifferentInputs_ProduceDifferentHashes()
    {
        Assert.NotEqual(
            RendererFeatureSet.FeatureSetHash(new[] { "A" }),
            RendererFeatureSet.FeatureSetHash(new[] { "B" }));
    }

    [Fact]
    public void FeatureSetHash_SeparatorDisambiguates_BoundaryCollisions()
    {
        // Without the '|' separator fold: hash(["AB"]) == hash(["A","B"])
        // because FNV-1a folding is purely character-stream. With our
        // separator fold, they diverge so cache keys for distinct feature
        // sets do not collide.
        Assert.NotEqual(
            RendererFeatureSet.FeatureSetHash(new[] { "AB" }),
            RendererFeatureSet.FeatureSetHash(new[] { "A", "B" }));
    }
}
