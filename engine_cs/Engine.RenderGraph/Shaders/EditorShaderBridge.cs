// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Engine.RenderGraph.Shaders;

/// <summary>Process-wide static bridge the editor's
/// <c>PluginCatalogService</c> uses to push active shader-feature cliArgs
/// into runtime code. Lives in the <c>Engine.Renderer</c> assembly so both
/// <c>Engine.Renderer</c> (which subscribes) and <c>Engine.Editor</c>
/// (which already ProjectReferences <c>Engine.Renderer</c>) can call it
/// without creating a circular project reference.</summary>
public static class EditorShaderBridge
{
    /// <summary>Invoked whenever the editor's enabled-plugin set changes.
    /// Carries the cliArgs argv (computed by
    /// <see cref="Engine.Renderer.RendererFeatureSet.BuildCliArgs"/>)
    /// AND the resolved Slang <c>-I</c> include-path list (computed by
    /// <see cref="Engine.Renderer.ShaderIncludeResolver.Resolve"/>) over
    /// the current enabled-plugin manifest set, bundled in a single
    /// coordinated push. Bundling prevents the "args pushed but dirs stale
    /// (or vice versa)" race that two parallel events fired off the same
    /// <c>SetEnabled</c> call would otherwise cause.</summary>
    public static event Action<IReadOnlyList<string>?, IReadOnlyList<string>?>?
        ActiveShaderContextChanged;

    public static IReadOnlyList<string>? LastCliArgs { get; private set; }
    public static IReadOnlyList<string>? LastIncludeDirs { get; private set; }

    /// <summary>Public raise seam: external emitters (the editor's
    /// <c>PluginCatalogService</c>) call this to forward both the
    /// currently-active cliArgs argv AND the resolved include dirs in
    /// a single coordinated push. C# events restrict <c>Invoke()</c> to
    /// the declaring assembly, so a wrapper method is required for an
    /// in-Editor assembly to fire the event on behalf of the in-Renderer
    /// assembly's listener.</summary>
    public static void RaiseActiveShaderContextChanged(
        IReadOnlyList<string>? cliArgs,
        IReadOnlyList<string>? includeDirs)
    {
        LastCliArgs = cliArgs;
        LastIncludeDirs = includeDirs;
        ActiveShaderContextChanged?.Invoke(cliArgs, includeDirs);
    }
}
