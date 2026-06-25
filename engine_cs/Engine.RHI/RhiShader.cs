// SPDX-License-Identifier: MIT
// Managed shader wrapper. Holds source text alive via pinned handle.

using System;
using System.Runtime.InteropServices;
using System.Text;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class RhiShader : IDisposable
{
    public IntPtr Handle { get; }

    // Keep source + entry alive for the shader's lifetime.
    private GCHandle _sourcePin;
    private GCHandle _entryPin;

    internal RhiShader(IntPtr handle)
    {
        Handle = handle;
    }

    public static RhiShader FromSource(RhiDevice device, string source, string entry)
    {
        if (string.IsNullOrEmpty(source))
            throw new ArgumentNullException(nameof(source));
        if (string.IsNullOrEmpty(entry))
            throw new ArgumentNullException(nameof(entry));

        byte[] srcBytes = Encoding.UTF8.GetBytes(source);
        byte[] entryBytes = Encoding.UTF8.GetBytes(entry + "\0");

        var sourceHandle = GCHandle.Alloc(srcBytes, GCHandleType.Pinned);
        var entryHandle  = GCHandle.Alloc(entryBytes, GCHandleType.Pinned);

        var desc = new RhiNative.ShaderDesc
        {
            Abi = 1,
            Stages = RhiNative.ShaderStage.Vertex | RhiNative.ShaderStage.Fragment,
            Source = sourceHandle.AddrOfPinnedObject(),
            SourceLen = (uint)srcBytes.Length,
            EntryPoint = entryHandle.AddrOfPinnedObject(),
        };

        int rc = RhiNative.RhiCreateShader(device.Handle, in desc, out IntPtr sh);
        if (rc != 0)
        {
            sourceHandle.Free();
            entryHandle.Free();
            throw new InvalidOperationException($"rhi_create_shader rc={rc} (entry={entry})");
        }

        var instance = new RhiShader(sh);
        instance._sourcePin = sourceHandle;
        instance._entryPin  = entryHandle;
        return instance;
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        RhiNative.RhiDestroyShader(Handle);
        if (_sourcePin.IsAllocated) _sourcePin.Free();
        if (_entryPin.IsAllocated)  _entryPin.Free();
        GC.SuppressFinalize(this);
    }
}
