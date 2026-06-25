// SPDX-License-Identifier: MIT
// Managed shader wrapper. Holds source text alive via pinned handle.

using System;
using System.Runtime.InteropServices;
using System.Text;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiShader : IDisposable
{
    public IntPtr Handle { get; private set; }

    // Keep source + entry alive for the shader's lifetime.
    private GCHandle _sourcePin;
    private GCHandle _entryPin;

    internal RhiShader(IntPtr handle, GCHandle source, GCHandle entry)
    {
        Handle     = handle;
        _sourcePin = source;
        _entryPin  = entry;
    }

    public static RhiShader FromSource(RhiDevice device, string source, string entry)
    {
        if (string.IsNullOrEmpty(source))
            throw new ArgumentNullException(nameof(source));
        if (string.IsNullOrEmpty(entry))
            throw new ArgumentNullException(nameof(entry));

        byte[] srcBytes   = Encoding.UTF8.GetBytes(source);
        byte[] entryBytes = Encoding.UTF8.GetBytes(entry + "\0");

        // Wrap both GCHandle.Alloc calls + native interop in try/catch so a
        // single OOM or P/Invoke exception mid-construction cannot leak a
        // pinned source handle. The original FromSource left sourceHandle in
        // a local that went out of scope on failure - a real leak path.
        GCHandle sourceHandle = GCHandle.Alloc(srcBytes,   GCHandleType.Pinned);
        GCHandle entryHandle  = default;
        try
        {
            entryHandle = GCHandle.Alloc(entryBytes, GCHandleType.Pinned);

            var desc = new RhiNative.ShaderDesc
            {
                Abi        = 1,
                Stages     = RhiNative.ShaderStage.Vertex | RhiNative.ShaderStage.Fragment,
                Source     = sourceHandle.AddrOfPinnedObject(),
                SourceLen  = (uint)srcBytes.Length,
                EntryPoint = entryHandle.AddrOfPinnedObject(),
            };

            int rc = RhiNative.RhiCreateShader(device.Handle, in desc, out IntPtr sh);
            if (rc != 0)
            {
                // Native create returned non-zero. Surface the failure to the
                // caller; cleanup happens in the catch below.
                throw new InvalidOperationException(
                    $"rhi_create_shader rc={rc} (entry={entry})");
            }

            // Hand BOTH handles to the new instance so the instance's
            // finalizer owns them. C# cannot throw between `new` and
            // `return`, so the catch block (which runs on any exception
            // inside the try) sees both handles as still allocated.
            return new RhiShader(sh, sourceHandle, entryHandle);
        }
        catch
        {
            // Free in reverse order of allocation. If we successfully
            // returned an instance above, both locals were reset to
            // `default` and IsAllocated is false, so this is a no-op.
            if (entryHandle.IsAllocated)  entryHandle.Free();
            if (sourceHandle.IsAllocated) sourceHandle.Free();
            throw;
        }
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero && !_sourcePin.IsAllocated && !_entryPin.IsAllocated) return;

        // Zero the managed handle BEFORE invoking the native destroy. If the
        // C-side destroy ever threw (assertion failure or free() failure),
        // a subsequent finalizer call would see Handle == 0 and skip the
        // duplicate rhi_destroy_shader call. The reverse order would risk a
        // double-free.
        IntPtr h = Handle;
        Handle = IntPtr.Zero;
        if (h != IntPtr.Zero) RhiNative.RhiDestroyShader(h);

        if (_sourcePin.IsAllocated) _sourcePin.Free();
        if (_entryPin.IsAllocated)  _entryPin.Free();
        GC.SuppressFinalize(this);
    }

    /// <summary>Safety net: see <see cref="RhiBuffer"/>.</summary>
    ~RhiShader() => Dispose();
}
