// SPDX-License-Identifier: MIT
// NativeLibraryLoader: a DllImport resolver that locates libEngineC.dylib
// at runtime. The previous Engine.CBindings.csproj used <NativeReference>
// which only works under .NET Framework MSBuild; this file replaces that
// dependency with a runtime resolver that knows the standard smoke-test
// layout (out/libEngineC.dylib next to the managed DLL).
//
// On macOS the AOT/JIT loader would probe a few standard locations, but we
// also support probing relative paths under out/ for the smoke test.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Engine.CBindings;

public static class NativeLibraryLoader
{
    // [ModuleInitializer] runs exactly once, the first time any code in this
    // assembly is touched - well before any [LibraryImport] fires - so the
    // resolver is armed before the first P/Invoke call.
    [ModuleInitializer]
    public static void Register()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "EngineC")
            return TryLibEngineC();
        return IntPtr.Zero;
    }

    private static IntPtr TryLibEngineC()
    {
        // 1. The managed DLL's directory (CopyToOutputDirectory already
        //    drops out/libEngineC.dylib here on a working build).
        string managedDir = Path.GetDirectoryName(typeof(NativeLibraryLoader).Assembly.Location)
                            ?? AppContext.BaseDirectory;
        string[] names = OperatingSystem.IsMacOS()
            ? new[] { "libEngineC.dylib", "libEngineC.dylib.dylib" }
            : (OperatingSystem.IsWindows()
                ? new[] { "EngineC.dll" }
                : new[] { "libEngineC.so" });
        foreach (var n in names)
        {
            string p = Path.Combine(managedDir, n);
            if (NativeLibrary.TryLoad(p, out IntPtr h)) return h;
        }

        // 2. Smoke-test fallback: walk up to out/ from cwd + base dir.
        string[] roots = { AppContext.BaseDirectory, managedDir, Directory.GetCurrentDirectory() };
        foreach (var root in roots)
        {
            string dir = Path.GetFullPath(Path.Combine(root, "..", ".."));
            foreach (var n in names)
            {
                string p = Path.Combine(dir, n);
                if (NativeLibrary.TryLoad(p, out IntPtr h)) return h;
            }
        }

        // 3. Let dlopen's default path do it.
        return IntPtr.Zero;
    }
}
