// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using static Engine.CBindings.Log;

namespace Engine.Plugin.SurfaceAuditor;

/// <summary>
/// Probes the four hardcoded engine-assembly allow-lists that must stay in
/// sync and reports drift via Warn log lines. Surfaces the same drift
/// class that produced the "could not load assembly engine.renderer"
/// runtime spam, as an editor menu-driven audit at
/// Tools > Run Surface Audit.
/// </summary>
internal static class DriftProbe
{
    private const string Prefix = "[SurfaceAuditor/DriftProbe] ";

    private static readonly (string Label, string Path)[] Sources =
    [
        ("GameAssemblyLoadContext.Load allow-list",
            "Editor/GameAssemblyLoadContext.cs"),
        ("PluginContext.Load allow-list",
            "engine_cs/Engine.Renderer/RendererPluginRuntime.cs"),
        ("Editor ProjectReferences",
            "Editor/Engine.Editor.csproj"),
        ("WelcomeWindow csproj template",
            "Editor/Views/WelcomeWindow.axaml.cs"),
    ];

    private static readonly (string Assembly, Regex Pattern)[] KnownAssemblies =
    [
        ("Engine.RHI",         new Regex(@"\bEngine\.RHI\b",         RegexOptions.Compiled)),
        ("Engine.RenderGraph", new Regex(@"\bEngine\.RenderGraph\b", RegexOptions.Compiled)),
        ("Engine.Scene",       new Regex(@"\bEngine\.Scene\b",       RegexOptions.Compiled)),
        ("Engine.Assets",      new Regex(@"\bEngine\.Assets\b",      RegexOptions.Compiled)),
        ("Engine.Plugins",     new Regex(@"\bEngine\.Plugins\b",     RegexOptions.Compiled)),
        ("Engine.Renderer",    new Regex(@"\bEngine\.Renderer\b",    RegexOptions.Compiled)),
        ("Engine.CBindings",   new Regex(@"\bEngine\.CBindings\b",   RegexOptions.Compiled)),
    ];

    public static void Run(string engineRoot)
    {
        if (string.IsNullOrEmpty(engineRoot))
        {
            Warn(Prefix + "engine root not set; skipping.", "SurfaceAuditor");
            return;
        }

        var sourceContents = new Dictionary<string, string>(StringComparer.Ordinal);
        var missingSources = new List<string>();
        foreach (var (label, relPath) in Sources)
        {
            string absPath = Path.Combine(engineRoot, relPath);
            string content;
            try
            {
                if (!File.Exists(absPath))
                {
                    missingSources.Add($"{label} ({relPath})");
                    continue;
                }
                content = File.ReadAllText(absPath);
            }
            catch (Exception ex)
            {
                Warn(
                    Prefix +
                        $"{label}: failed to read {relPath} ({ex.GetType().Name}: {ex.Message})",
                    "SurfaceAuditor");
                continue;
            }
            sourceContents[label] = content;
        }

        var drifts = new List<string>();
        foreach (var (assembly, pattern) in KnownAssemblies)
        {
            var absentFrom = new List<string>();
            foreach (var (label, _) in Sources)
            {
                if (!sourceContents.TryGetValue(label, out var content) ||
                    !pattern.IsMatch(content))
                {
                    absentFrom.Add(label);
                }
            }
            if (absentFrom.Count > 0)
            {
                drifts.Add(
                    $"{assembly} missing in {absentFrom.Count}/{Sources.Length} source(s): [{string.Join(", ", absentFrom)}]");
            }
        }

        if (missingSources.Count > 0)
        {
            Warn(
                Prefix + "Missing source files: " + string.Join("; ", missingSources),
                "SurfaceAuditor");
        }
        if (drifts.Count == 0)
        {
            Info(
                Prefix +
                    $"All {KnownAssemblies.Length} engine assemblies present in every discovered source.",
                "SurfaceAuditor");
            return;
        }
        Warn(
            Prefix +
                $"{drifts.Count}/{KnownAssemblies.Length} engine assembly drift(s) detected:",
            "SurfaceAuditor");
        foreach (string d in drifts)
            Warn(Prefix + "  - " + d, "SurfaceAuditor");
    }
}
