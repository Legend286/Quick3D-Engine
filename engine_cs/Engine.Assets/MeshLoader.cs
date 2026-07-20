using System;
using System.IO;
using System.Runtime.InteropServices;
using Engine.RHI;
using Engine.CBindings;

namespace Engine.Assets;

public struct Vertex
{
    public float px, py, pz;
    public float nx, ny, nz;
    public float tu, tv;
    public float tx, ty, tz, tw;
}

public class Mesh
{
    public RhiBuffer VertexBuffer;
    public RhiBuffer IndexBuffer;
    public uint VertexCount;
    public uint IndexCount;
    public uint IndexFormat; // 16 or 32
    public RhiAccelStruct? Blas;

    public Mesh(RhiBuffer vb, RhiBuffer ib, uint vc, uint ic, uint ifmt)
    {
        VertexBuffer = vb;
        IndexBuffer = ib;
        VertexCount = vc;
        IndexCount = ic;
        IndexFormat = ifmt;
    }
}

public static class MeshLoader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MeshHeader
    {
        public uint Magic;      // 'MSH1' -> 0x3148534D
        public uint VertexCount;
        public uint IndexCount;
        public uint IndexFormat;
    }

    private static readonly System.Collections.Generic.Dictionary<string, Mesh> _cache = new();
    private static readonly object _lock = new();

    public static void ClearCache() 
    {
        lock (_lock) _cache.Clear();
    }

    public static unsafe Mesh LoadMsh(RhiDevice device, string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Mesh not found: {path}");

        string fullPath = Path.GetFullPath(path);
        lock (_lock)
        {
            if (_cache.TryGetValue(fullPath, out var cached)) return cached;
        }

        byte[] fileBytes = File.ReadAllBytes(path);
        fixed (byte* ptr = fileBytes)
        {
            MeshHeader* header = (MeshHeader*)ptr;
            if (header->Magic != 0x3148534D)
                throw new InvalidDataException("Invalid .msh file magic.");

            ulong iSize = (ulong)header->IndexCount * (header->IndexFormat == 16 ? 2ul : 4ul);
            ulong expectedVSize = (ulong)fileBytes.Length - 16ul - iSize;
            int stride = (int)(expectedVSize / header->VertexCount);

            ulong vSizeTarget = (ulong)header->VertexCount * (ulong)sizeof(Vertex);

            RhiBuffer vb = RhiBuffer.Create(device, vSizeTarget, RhiNative.BufferUsage.Vertex | RhiNative.BufferUsage.Storage);
            RhiBuffer ib = RhiBuffer.Create(device, iSize, RhiNative.BufferUsage.Index | RhiNative.BufferUsage.Storage);

            if (stride == 32)
            {
                // Upgrade 32-byte vertex to 48-byte vertex
                Vertex[] upgraded = new Vertex[header->VertexCount];
                float* oldV = (float*)(ptr + 16);
                for (int i = 0; i < header->VertexCount; i++)
                {
                    upgraded[i] = new Vertex
                    {
                        px = oldV[i * 8 + 0], py = oldV[i * 8 + 1], pz = oldV[i * 8 + 2],
                        nx = oldV[i * 8 + 3], ny = oldV[i * 8 + 4], nz = oldV[i * 8 + 5],
                        tu = oldV[i * 8 + 6], tv = oldV[i * 8 + 7],
                        tx = 1.0f, ty = 0.0f, tz = 0.0f, tw = 1.0f // Default tangent
                    };
                }
                fixed (Vertex* upPtr = upgraded)
                {
                    vb.Upload((IntPtr)upPtr, vSizeTarget);
                }
            }
            else if (stride == sizeof(Vertex))
            {
                vb.Upload(new IntPtr(ptr + 16), vSizeTarget);
            }
            else
            {
                throw new InvalidDataException($"Unknown vertex stride {stride}");
            }

            ib.Upload(new IntPtr(ptr + 16 + expectedVSize), iSize);

            var mesh = new Mesh(vb, ib, header->VertexCount, header->IndexCount, header->IndexFormat);
            lock (_lock)
            {
                _cache[fullPath] = mesh;
            }
            return mesh;
        }
    }
}
