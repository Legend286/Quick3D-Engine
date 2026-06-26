// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;

namespace Engine.Editor;

public sealed class GameAssemblyLoadContext : AssemblyLoadContext
{
    private readonly string _assemblyPath;

    public GameAssemblyLoadContext(string assemblyPath) : base(isCollectible: true)
    {
        _assemblyPath = assemblyPath;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, "Engine.Game", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = File.ReadAllBytes(_assemblyPath);
            using var stream = new MemoryStream(bytes);

            string pdbPath = Path.ChangeExtension(_assemblyPath, ".pdb");
            if (File.Exists(pdbPath))
            {
                var pdbBytes = File.ReadAllBytes(pdbPath);
                using var pdbStream = new MemoryStream(pdbBytes);
                return LoadFromStream(stream, pdbStream);
            }
            return LoadFromStream(stream);
        }

        if (string.Equals(assemblyName.Name, "Engine.RHI", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assemblyName.Name, "Engine.RenderGraph", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assemblyName.Name, "Engine.Scene", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(assemblyName.Name, "Engine.CBindings", StringComparison.OrdinalIgnoreCase))
        {
            return Default.LoadFromAssemblyName(assemblyName);
        }

        return null;
    }
}

