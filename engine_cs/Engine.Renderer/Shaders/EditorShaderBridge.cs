// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Engine.Renderer.Shaders;

/// <summary>Process-wide static bridge the editor's
/// <c>PluginCatalogService</c> uses to push active shader-feature cliArgs
/// into runtime code. Lives in the <c>Engine.Renderer</c> assembly so both
/// <c>Engine.Renderer</c> (which subscribes) and <c>Engine.Editor</c>
/// (which already ProjectReferences <c>Engine.Renderer</c>) can call it
/// without creating a circular project reference.</summary>
public static class EditorShaderBridge
{
    /// <summary>Invoked whenever the editor's enabled-plugin set changes.
    /// Subscribers (typically the <c>Renderer</c>) update their internal
    /// feature cache and trigger the affected shader compile pipeline to
    /// refresh. Argument is the result of
    /// <see cref="Engine.Renderer.Shaders.RendererFeatureSet.BuildCliArgs"/>
    /// over the current enabled-plugin manifest set.</summary>
    public static event Action<IReadOnlyList<string>?>?
        ActiveShaderCliArgsChanged;

    /// <summary>Public raise seam: external emitters (the editor's
    /// <c>PluginCatalogService</c>) call this to forward the
    /// currently-active feature-set argv. C# events restrict
    /// <c>Invoke()</c> to the declaring assembly, so a wrapper method
    /// is required for an in-Editor assembly to fire the event on
    /// behalf of the in-Renderer assembly's listener.</summary>
    public static void RaiseActiveShaderCliArgsChanged(
        IReadOnlyList<string>? cliArgs)
        => ActiveShaderCliArgsChanged?.Invoke(cliArgs);
}
