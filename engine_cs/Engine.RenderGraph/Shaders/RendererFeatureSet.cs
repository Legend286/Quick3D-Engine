// SPDX-License-Identifier: MIT
// Plugin-feature-set -> Slang -D NAME=1 argv compiler.

using System;
using System.Collections.Generic;
using Engine.Plugins;

namespace Engine.RenderGraph.Shaders;

/// <summary>
/// Resolves the active set of plugin-declared shader preprocessor feature
/// flags into the Slang <c>-D NAME=1</c> argv tokens threaded through
/// <see cref="Engine.RHI.RhiShader.FromSource"/>.
/// </summary>
public static class RendererFeatureSet
{
    /// <summary>
    /// Builds the ordered argv token list that the rendering layer should
    /// pass as <c>cliArgs</c> to <see cref="Engine.RHI.RhiShader.FromSource"/>.
    /// Each enabled plugin's <c>ShaderFeatures</c> entries are deduped
    /// (Ordinally compared, sorted ascending for stable cache keys),
    /// filtered for whitespace-only / empty entries, and expanded into
    /// two-token <c>-D NAME=1</c> pairs.
    /// </summary>
    /// <param name="plugins">Sequence of (manifest, isEnabled) pairs. Null
    /// is allowed and treated as empty. Each manifest is consulted exactly
    /// once. Plugins with isEnabled=false are skipped wholesale (rather
    /// than per-feature) so an entirely-disabled plugin contributes zero
    /// tokens.</param>
    /// <returns>Null when no plugin declares an active feature, which the
    /// caller forwards to RhiShader as "no extra CLI args". Otherwise a
    /// list with even length (<c>-D</c> followed by <c>NAME=1</c>).</returns>
    public static IReadOnlyList<string>? BuildCliArgs(
        IEnumerable<(EnginePluginManifest Manifest, bool IsEnabled)>? plugins)
    {
        if (plugins == null) return null;

        SortedSet<string> activeFeatures = new(StringComparer.Ordinal);
        foreach (var (manifest, isEnabled) in plugins)
        {
            if (!isEnabled) continue;
            if (manifest.ShaderFeatures == null) continue;
            foreach (string raw in manifest.ShaderFeatures)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                activeFeatures.Add(raw.Trim());
            }
        }

        if (activeFeatures.Count == 0) return null;

        List<string> argv = new(activeFeatures.Count * 2);
        foreach (string name in activeFeatures)
        {
            argv.Add("-D");
            argv.Add(name + "=1");
        }
        return argv;
    }

    /// <summary>
    /// Stable FNV-1a 32-bit hash over a sorted feature list. Used by
    /// <see cref="ShaderCompileCache"/> to derive cache keys invariant under
    /// caller-supplied sort order. Returns 0 for null/empty inputs (the
    /// canonical "no plugin features" value).
    /// </summary>
    /// <remarks>The input is expected to be sorted ascendingly (matching
    /// <see cref="BuildCliArgs"/>'s output). A pipe separator <c>|</c> is
    /// folded into each feature boundary so distinct multi-feature
    /// combinations never collide (e.g. <c>["AB"]</c> vs <c>["A","B"]</c>).</remarks>
    public static int FeatureSetHash(IReadOnlyList<string>? sortedFeatures)
    {
        if (sortedFeatures == null || sortedFeatures.Count == 0) return 0;
        unchecked
        {
            int hash = (int)0x811C9DC5;
            foreach (string name in sortedFeatures)
            {
                foreach (char c in name)
                {
                    hash ^= c;
                    hash *= 0x01000193;
                }
                hash ^= '|';
                hash *= 0x01000193;
            }
            return hash;
        }
    }
}
