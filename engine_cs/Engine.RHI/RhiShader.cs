// SPDX-License-Identifier: MIT
// Managed shader wrapper. Holds source text alive via pinned handle.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiShader : IDisposable
{
    public IntPtr Handle { get; private set; }

    // Keep source + entry + include + cliArgs alive for the shader's lifetime.
    private GCHandle _sourcePin;
    private GCHandle _entryPin;
    private GCHandle _includePin;
    private GCHandle _cliArgsPin;

    internal RhiShader(IntPtr handle, GCHandle source, GCHandle entry, GCHandle includeHandle, GCHandle cliArgsHandle)
    {
        Handle = handle;
        _sourcePin = source;
        _entryPin = entry;
        _includePin = includeHandle;
        _cliArgsPin = cliArgsHandle;
    }

    public static RhiShader FromSource(RhiDevice device, string source, string entry, RhiNative.ShaderStage stage, string? includePath = null)
        => CreateFromSourceCore(device, source, entry, stage, includePath, cliArgs: null);

    /// <summary>
    /// Compiles a shader from in-memory Slang source with an ordered list of
    /// include directories and an ordered list of raw Slang CLI arguments
    /// appended after the engine's positional tokens.
    /// </summary>
    /// <param name="device">The RHI device that will own the resulting shader.</param>
    /// <param name="source">The full Slang source text.</param>
    /// <param name="entry">The entry-point identifier (e.g. <c>VSMain</c>).</param>
    /// <param name="stage">The shader stage mask.</param>
    /// <param name="includeDirs">Optional ordered list of include directories.
    /// The first directory in the list has highest resolution priority; the
    /// engine's <c>ContentRoot/shaders</c> is implicitly appended last by
    /// callers like <see cref="Engine.Renderer.ShaderIncludeResolver"/> when
    /// they hand the list off here.</param>
    /// <param name="cliArgs">Optional ordered list of raw Slang CLI arguments
    /// (e.g. <c>["-D", "FOO=1", "-O0"]</c>) appended to the Slang preprocessor
    /// pipeline. None of the entries may contain whitespace; the helper
    /// <see cref="JoinCliArgs"/> below concatenates with single spaces and
    /// the Metal backend re-tokenises on whitespace.</param>
    public static RhiShader FromSource(RhiDevice device, string source, string entry, RhiNative.ShaderStage stage,
                                        IReadOnlyList<string>? includeDirs, IReadOnlyList<string>? cliArgs)
        => CreateFromSourceCore(device, source, entry, stage,
            JoinIncludePaths(includeDirs), JoinCliArgs(cliArgs));

    /// <summary>
    /// Sentinel separator used to pack multiple include directories into a
    /// single C-ABI string parameter. Chosen for low collision risk against
    /// any plausible filesystem path; the Metal backend splits on this
    /// sequence and emits one <c>-I</c> flag per entry. Authoritative token
    /// on the C side is <c>RHI_SHADER_INCLUDE_PATH_SEPARATOR</c> in
    /// <c>engine_c/rhi/rhi.h</c>.
    /// </summary>
    internal const string MultiPathSeparator = ";;--;;";

    /// <summary>
    /// Joins an ordered list of include directories into a single string
    /// suitable for the <see cref="FromSource(RhiDevice, string, string, RhiNative.ShaderStage, string?)"/>
    /// overload's <c>includePath</c> argument. Null or empty inputs return
    /// <c>null</c>; a single-element list returns that element verbatim;
    /// a multi-element list returns the entries joined by
    /// <see cref="MultiPathSeparator"/>. Caller-side consumers such as
    /// <see cref="Engine.Renderer.ShaderIncludeResolver"/> may use this to
    /// flatten a priority-ordered path list before invoking
    /// <c>FromSource</c>.
    /// </summary>
    internal static string? JoinIncludePaths(IReadOnlyList<string>? paths)
    {
        if (paths == null || paths.Count == 0)
            return null;
        if (paths.Count == 1)
            return paths[0];
        return string.Join(MultiPathSeparator, paths);
    }

    /// <summary>
    /// Joins an ordered list of raw Slang CLI arguments into a single
    /// whitespace-separated string suitable for the multi-arg
    /// <see cref="FromSource(RhiDevice, string, string, RhiNative.ShaderStage, IReadOnlyList{string}?, IReadOnlyList{string}?)"/>
    /// overload's <c>cliArgs</c> argument. Null or empty inputs return
    /// <c>null</c>; a single-element list returns that element verbatim;
    /// a multi-element list returns the entries joined by single space.
    /// Caller is responsible for ensuring each entry is a single token
    /// (i.e. does not contain whitespace) so the Metal backend's
    /// whitespace-tokenising code re-emits the intended argv slots. Empty
    /// entries are preserved as consecutive delimiters so misuse with empty
    /// tokens is detectable downstream rather than silently collapsed.
    /// </summary>
    public static string? JoinCliArgs(IReadOnlyList<string>? cliArgs)
    {
        if (cliArgs == null || cliArgs.Count == 0)
            return null;
        if (cliArgs.Count == 1)
            return cliArgs[0];
        return string.Join(" ", cliArgs);
    }

    private static RhiShader CreateFromSourceCore(RhiDevice device, string source, string entry, RhiNative.ShaderStage stage,
                                                   string? includePath, string? cliArgs)
    {
        if (string.IsNullOrEmpty(source))
            throw new ArgumentNullException(nameof(source));
        if (string.IsNullOrEmpty(entry))
            throw new ArgumentNullException(nameof(entry));

        byte[] srcBytes = Encoding.UTF8.GetBytes(source + "\0");
        byte[] entryBytes = Encoding.UTF8.GetBytes(entry + "\0");
        byte[]? includeBytes = includePath != null ? Encoding.UTF8.GetBytes(includePath + "\0") : null;
        byte[]? cliArgsBytes = cliArgs != null ? Encoding.UTF8.GetBytes(cliArgs + "\0") : null;

        // Wrap all GCHandle.Alloc calls + native interop in try/catch so a
        // single OOM or P/Invoke exception mid-construction cannot leak a
        // pinned source handle. The original FromSource left sourceHandle in
        // a local that went out of scope on failure - a real leak path.
        GCHandle sourceHandle = GCHandle.Alloc(srcBytes, GCHandleType.Pinned);
        GCHandle entryHandle = default;
        GCHandle includeHandle = default;
        GCHandle cliArgsHandle = default;
        try
        {
            entryHandle = GCHandle.Alloc(entryBytes, GCHandleType.Pinned);
            if (includeBytes != null)
                includeHandle = GCHandle.Alloc(includeBytes, GCHandleType.Pinned);
            if (cliArgsBytes != null)
                cliArgsHandle = GCHandle.Alloc(cliArgsBytes, GCHandleType.Pinned);

            var desc = new RhiNative.ShaderDesc
            {
                Abi = 1,
                Stages = stage,
                Source = sourceHandle.AddrOfPinnedObject(),
                SourceLen = (uint)srcBytes.Length,
                EntryPoint = entryHandle.AddrOfPinnedObject(),
                IncludePath = includeBytes != null ? includeHandle.AddrOfPinnedObject() : IntPtr.Zero,
                CliArgs = cliArgsBytes != null ? cliArgsHandle.AddrOfPinnedObject() : IntPtr.Zero,
            };

            int rc = RhiNative.RhiCreateShader(device.Handle, in desc, out IntPtr sh);
            if (rc != 0)
            {
                // Native create returned non-zero. Surface the failure to the
                // caller; cleanup happens in the catch below.
                throw new InvalidOperationException(
                    $"rhi_create_shader rc={rc} (entry={entry})");
            }

            // Hand ALL handles to the new instance so the instance's
            // finalizer owns them. C# cannot throw between `new` and
            // `return`, so the catch block (which runs on any exception
            // inside the try) sees all handles as still allocated.
            return new RhiShader(sh, sourceHandle, entryHandle, includeHandle, cliArgsHandle);
        }
        catch
        {
            // Free in reverse order of allocation. If we successfully
            // returned an instance above, the locals were captured by the
            // returned object and IsAllocated is false, so this is a no-op.
            if (cliArgsHandle.IsAllocated) cliArgsHandle.Free();
            if (includeHandle.IsAllocated) includeHandle.Free();
            if (entryHandle.IsAllocated) entryHandle.Free();
            if (sourceHandle.IsAllocated) sourceHandle.Free();
            throw;
        }
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero && !_sourcePin.IsAllocated && !_entryPin.IsAllocated
            && !_includePin.IsAllocated && !_cliArgsPin.IsAllocated) return;

        // Zero the managed handle BEFORE invoking the native destroy. If the
        // C-side destroy ever threw (assertion failure or free() failure),
        // a subsequent finalizer call would see Handle == 0 and skip the
        // duplicate rhi_destroy_shader call. The reverse order would risk a
        // double-free.
        IntPtr h = Handle;
        Handle = IntPtr.Zero;
        if (h != IntPtr.Zero) RhiNative.RhiDestroyShader(h);

        if (_cliArgsPin.IsAllocated) _cliArgsPin.Free();
        if (_includePin.IsAllocated) _includePin.Free();
        if (_entryPin.IsAllocated) _entryPin.Free();
        if (_sourcePin.IsAllocated) _sourcePin.Free();
        GC.SuppressFinalize(this);
    }

    private string? _debugName;
    private string _debugCategory = "Shader";

    /// <summary>Setter pair with <see cref="RhiBuffer.SetDebugName"/>.
    /// Records the label in managed-side storage keyed by this
    /// instance's <see cref="Handle"/> so renderer diagnostics can
    /// surface shader provenance without the C RHI round-trip the
    /// buffer/texture paths perform via <c>GpuResourceRegistry</c>.</summary>
    public void SetDebugName(string name, string category = "Shader")
    {
        _debugName = name ?? throw new ArgumentNullException(nameof(name));
        _debugCategory = category ?? "Shader";
    }

    /// <summary>Gets the most-recent label assigned via
    /// <see cref="SetDebugName"/>, or <c>null</c> if no label was set.</summary>
    public string? DebugName => _debugName;

    /// <summary>Gets the diagnostic category assigned alongside the label.</summary>
    public string DebugCategory => _debugCategory;

    /// <summary>Safety net: see <see cref="RhiBuffer"/>.</summary>
    ~RhiShader() => Dispose();
}
