// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Engine.Game;

/// <summary>
/// Generates procedural UV sphere and plane meshes at runtime and writes them
/// as .msh binary files compatible with MeshLoader.LoadMsh.
/// See docs/asset-pipeline/formats.md for the .msh layout.
/// Header: Magic(u32) VertexCount(u32) IndexCount(u32) IndexFormat(u32) = 16 bytes
/// Vertex: px py pz nx ny nz tu tv tx ty tz tw = 48 bytes (12 x float32)
/// </summary>
public static class PrimitiveMeshFactory
{
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    private struct MeshHeader
    {
        public uint Magic;       // 0x3148534D ('MSH1' little-endian)
        public uint VertexCount;
        public uint IndexCount;
        public uint IndexFormat; // 16 or 32
    }

    [StructLayout(LayoutKind.Sequential, Size = 48)]
    private struct MshVertex
    {
        public float Px, Py, Pz;
        public float Nx, Ny, Nz;
        public float Tu, Tv;
        public float Tx, Ty, Tz, Tw;
    }

    public static string GenerateUVSphere(string outputPath, int stacks = 32, int slices = 32)
    {
        var vertices = new System.Collections.Generic.List<MshVertex>();
        var indices  = new System.Collections.Generic.List<uint>();

        for (int stack = 0; stack <= stacks; stack++)
        {
            float phi    = MathF.PI * stack / stacks;
            float sinPhi = MathF.Sin(phi);
            float cosPhi = MathF.Cos(phi);

            for (int slice = 0; slice <= slices; slice++)
            {
                float theta    = 2.0f * MathF.PI * slice / slices;
                float sinTheta = MathF.Sin(theta);
                float cosTheta = MathF.Cos(theta);

                float x = sinPhi * cosTheta;
                float y = cosPhi;
                float z = sinPhi * sinTheta;

                float u = (float)slice / slices;
                float v = (float)stack / stacks;

                // Tangent is the derivative of position w.r.t. theta, normalized
                float tx = -sinTheta;
                float tz =  cosTheta;

                vertices.Add(new MshVertex
                {
                    Px = x, Py = y, Pz = z,
                    Nx = x, Ny = y, Nz = z,
                    Tu = u, Tv = v,
                    Tx = tx, Ty = 0.0f, Tz = tz, Tw = 1.0f
                });
            }
        }

        for (int stack = 0; stack < stacks; stack++)
        {
            for (int slice = 0; slice < slices; slice++)
            {
                uint a = (uint)( stack      * (slices + 1) + slice);
                uint b = (uint)((stack + 1) * (slices + 1) + slice);
                uint c = (uint)((stack + 1) * (slices + 1) + slice + 1);
                uint d = (uint)( stack      * (slices + 1) + slice + 1);

                indices.Add(a); indices.Add(b); indices.Add(c);
                indices.Add(a); indices.Add(c); indices.Add(d);
            }
        }

        WriteMsh(outputPath, vertices, indices);
        return outputPath;
    }

    public static string GeneratePlane(string outputPath, float width = 20.0f, float depth = 20.0f, int divisionsX = 1, int divisionsZ = 1)
    {
        var vertices = new System.Collections.Generic.List<MshVertex>();
        var indices  = new System.Collections.Generic.List<uint>();

        for (int z = 0; z <= divisionsZ; z++)
        {
            for (int x = 0; x <= divisionsX; x++)
            {
                float px = (x / (float)divisionsX - 0.5f) * width;
                float pz = (z / (float)divisionsZ - 0.5f) * depth;
                float u  = x / (float)divisionsX;
                float v  = z / (float)divisionsZ;

                vertices.Add(new MshVertex
                {
                    Px = px, Py = 0, Pz = pz,
                    Nx = 0,  Ny = 1, Nz = 0,
                    Tu = u,  Tv = v,
                    Tx = 1,  Ty = 0, Tz = 0, Tw = 1.0f
                });
            }
        }

        for (int z = 0; z < divisionsZ; z++)
        {
            for (int x = 0; x < divisionsX; x++)
            {
                uint a = (uint)( z      * (divisionsX + 1) + x);
                uint b = (uint)((z + 1) * (divisionsX + 1) + x);
                uint c = (uint)((z + 1) * (divisionsX + 1) + x + 1);
                uint d = (uint)( z      * (divisionsX + 1) + x + 1);

                indices.Add(a); indices.Add(b); indices.Add(c);
                indices.Add(a); indices.Add(c); indices.Add(d);
            }
        }

        WriteMsh(outputPath, vertices, indices);
        return outputPath;
    }

    private static unsafe void WriteMsh(string path, System.Collections.Generic.List<MshVertex> vertices, System.Collections.Generic.List<uint> indices)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var w  = new BinaryWriter(fs))
        {
            var header = new MeshHeader
            {
                Magic       = 0x3148534D,
                VertexCount = (uint)vertices.Count,
                IndexCount  = (uint)indices.Count,
                IndexFormat = 32,
            };

            byte[] hBytes = new byte[16];
            fixed (byte* bp = hBytes) Buffer.MemoryCopy(&header, bp, 16, 16);
            w.Write(hBytes);

            byte[] vBytes = new byte[vertices.Count * 48];
            fixed (byte* bp = vBytes)
            {
                MshVertex* dst = (MshVertex*)bp;
                for (int i = 0; i < vertices.Count; i++) dst[i] = vertices[i];
            }
            w.Write(vBytes);

            foreach (var idx in indices) w.Write(idx);
        }

        File.Move(tmp, path, overwrite: true);
    }
}
