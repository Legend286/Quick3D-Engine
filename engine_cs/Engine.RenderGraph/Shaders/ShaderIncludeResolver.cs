// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using System.IO;
using Engine.Plugins;

namespace Engine.RenderGraph.Shaders;

/// <summary>
/// Resolves a Slang <c>-I</c> include-path list from active plugin
/// manifests. Plugin-supplied directories take priority over the engine's
/// <c>ContentRoot/shaders</c> fallback so plugins can ship <c>*.slang</c>
/// include files that compose with host shaders (e.g. <c>pbr.slang</c>) via
/// Slang <c>#include</c> directives without forking the host source.
/// </summary>
/// <remarks>
/// Reservation order is preserve-first; duplicate plugin paths collapse to
/// their first occurrence so the resolver is stable across repeat calls.
/// Missing include directories are filtered silently — a plugin whose
/// <c>shader_includes</c> lists a stale entry won't take the engine down.
/// Callers that want a hard error on missing dirs should validate their
/// manifests elsewhere; the resolver is a happy-path helper.
/// </remarks>
public static class ShaderIncludeResolver
{
    /// <summary>
    /// Builds the ordered include-path list for a given render-graph
    /// compile. Plugin manifests are consumed in their enumeration order;
    /// each plugin's <see cref="EnginePluginManifest.ShaderIncludes"/>
    /// entries are resolved relative to that plugin's manifest directory
    /// before being appended.
    /// </summary>
    /// <param name="contentRoot">Project content root (e.g.
    /// <c>/path/to/MyProject/Content</c>). The engine's fallback shader
    /// directory is derived as <c>contentRoot/shaders</c>; absent, the
    /// fallback is skipped.</param>
    /// <param name="enabledPlugins">Pairs of
    /// <c>(manifest, manifestPath)</c>. The manifest's
    /// <see cref="EnginePluginManifest.ShaderIncludes"/> are resolved
    /// relative to the directory portion of <paramref name="manifestPath"/>.
    /// Both elements are required; pass empty to disable plugin
    /// contributions.</param>
    /// <returns>Priority-ordered full filesystem paths, plugin-first and
    /// engine fallback last. Empty when neither plugins nor the engine
    /// default supply any directories.</returns>
    public static IReadOnlyList<string> Resolve(
        string contentRoot,
        IEnumerable<(EnginePluginManifest Manifest, string ManifestPath)> enabledPlugins)
    {
        ArgumentNullException.ThrowIfNull(contentRoot);
        ArgumentNullException.ThrowIfNull(enabledPlugins);

        var resolved = new List<string>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var (manifest, manifestPath) in enabledPlugins)
        {
            if (manifest?.ShaderIncludes == null ||
                manifest.ShaderIncludes.Count == 0)
            {
                continue;
            }

            string? pluginRoot = SafeDirectoryName(manifestPath);
            if (string.IsNullOrEmpty(pluginRoot))
            {
                continue;
            }

            foreach (string relative in manifest.ShaderIncludes)
            {
                if (string.IsNullOrWhiteSpace(relative))
                {
                    continue;
                }

                string full = Path.IsPathRooted(relative)
                    ? relative
                    : Path.GetFullPath(
                        Path.Combine(pluginRoot, relative));
                full = Path.TrimEndingDirectorySeparator(full);
                if (string.IsNullOrEmpty(full))
                    full = Path.DirectorySeparatorChar.ToString();
                if (!Directory.Exists(full))
                {
                    continue;
                }
                if (seen.Add(full))
                {
                    resolved.Add(full);
                }
            }
        }

        // Engine default is appended LAST — lowest priority for Slang
        // #include resolution. Skipped silently when absent so the
        // resolver still works for tests / empty-content scenarios.
        if (!string.IsNullOrEmpty(contentRoot))
        {
            string[] possibleRoots = new[]
            {
                contentRoot,
                Path.Combine(AppContext.BaseDirectory, "Content"),
                Path.Combine(Environment.CurrentDirectory, "Content")
            };
            foreach (var root in possibleRoots)
            {
                string engineShaders = Path.Combine(root, "shaders");
                if (Directory.Exists(engineShaders) && seen.Add(engineShaders))
                {
                    resolved.Add(engineShaders);
                }
            }
        }

        return resolved;
    }

    private static string? SafeDirectoryName(string manifestPath)
    {
        if (string.IsNullOrEmpty(manifestPath))
        {
            return null;
        }
        try
        {
            return Path.GetDirectoryName(manifestPath);
        }
        catch
        {
            return null;
        }
    }
}
