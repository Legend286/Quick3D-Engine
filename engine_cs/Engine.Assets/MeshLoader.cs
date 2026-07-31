using System;
using System.IO;
using System.Numerics;
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

public class Mesh : IDisposable
{
    public RhiBuffer VertexBuffer;
    public RhiBuffer IndexBuffer;
    public uint VertexCount;
    public uint IndexCount;
    public uint IndexFormat; // 16 or 32
    public RhiAccelStruct? Blas;
    /// <summary>Gets the geometry-derived local sphere centre.</summary>
    public Vector3 BoundsSphereCenter;
    /// <summary>Gets the geometry-derived local sphere radius.</summary>
    public float BoundsSphereRadius;

    public Mesh(
        RhiBuffer vb,
        RhiBuffer ib,
        uint vc,
        uint ic,
        uint ifmt,
        Vector3 boundsSphereCenter,
        float boundsSphereRadius)
    {
        VertexBuffer = vb;
        IndexBuffer = ib;
        VertexCount = vc;
        IndexCount = ic;
        IndexFormat = ifmt;
        BoundsSphereCenter = boundsSphereCenter;
        BoundsSphereRadius = boundsSphereRadius;
    }

    public void Dispose()
    {
        VertexBuffer?.Dispose();
        IndexBuffer?.Dispose();
        Blas?.Dispose();
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

    private static unsafe (Vector3 Center, float Radius)
        CalculateBoundingSphere(
            byte* vertexData,
            uint vertexCount,
            int stride)
    {
        if (vertexCount == 0)
            return (Vector3.Zero, 0.001f);

        Vector3 first = ReadPosition(vertexData, stride, 0);
        Vector3 minimumX = first;
        Vector3 maximumX = first;
        Vector3 minimumY = first;
        Vector3 maximumY = first;
        Vector3 minimumZ = first;
        Vector3 maximumZ = first;
        for (uint index = 1; index < vertexCount; ++index)
        {
            Vector3 position =
                ReadPosition(vertexData, stride, index);
            if (position.X < minimumX.X) minimumX = position;
            if (position.X > maximumX.X) maximumX = position;
            if (position.Y < minimumY.Y) minimumY = position;
            if (position.Y > maximumY.Y) maximumY = position;
            if (position.Z < minimumZ.Z) minimumZ = position;
            if (position.Z > maximumZ.Z) maximumZ = position;
        }

        Vector3 diameterStart = minimumX;
        Vector3 diameterEnd = maximumX;
        float diameterSquared =
            Vector3.DistanceSquared(minimumX, maximumX);
        float yDiameterSquared =
            Vector3.DistanceSquared(minimumY, maximumY);
        if (yDiameterSquared > diameterSquared)
        {
            diameterStart = minimumY;
            diameterEnd = maximumY;
            diameterSquared = yDiameterSquared;
        }
        float zDiameterSquared =
            Vector3.DistanceSquared(minimumZ, maximumZ);
        if (zDiameterSquared > diameterSquared)
        {
            diameterStart = minimumZ;
            diameterEnd = maximumZ;
        }

        Vector3 center = (diameterStart + diameterEnd) * 0.5f;
        float radius = Vector3.Distance(
            diameterStart,
            diameterEnd) * 0.5f;
        for (uint index = 0; index < vertexCount; ++index)
        {
            Vector3 position =
                ReadPosition(vertexData, stride, index);
            Vector3 offset = position - center;
            float distance = offset.Length();
            if (distance <= radius || distance <= 0.0f)
                continue;
            float expandedRadius = (radius + distance) * 0.5f;
            center += offset *
                ((expandedRadius - radius) / distance);
            radius = expandedRadius;
        }

        return (center, MathF.Max(radius * 1.00001f, 0.001f));
    }

    private static unsafe Vector3 ReadPosition(
        byte* vertexData,
        int stride,
        uint index)
    {
        float* position =
            (float*)(vertexData + index * stride);
        return new Vector3(
            position[0],
            position[1],
            position[2]);
    }

    public static void ClearCache() 
    {
        lock (_lock)
        {
            foreach (var mesh in _cache.Values)
            {
                mesh.Dispose();
            }
            _cache.Clear();
        }
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
            (Vector3 sphereCenter, float sphereRadius) =
                CalculateBoundingSphere(
                    ptr + 16,
                    header->VertexCount,
                    stride);

            ulong vSizeTarget = (ulong)header->VertexCount * (ulong)sizeof(Vertex);

            RhiBuffer vb = RhiBuffer.Create(device, vSizeTarget, RhiNative.BufferUsage.Vertex | RhiNative.BufferUsage.Storage);
            RhiBuffer ib = RhiBuffer.Create(device, iSize, RhiNative.BufferUsage.Index | RhiNative.BufferUsage.Storage);
            string meshName = Path.GetFileName(path);
            vb.SetDebugName($"{meshName} vertices", "Model");
            ib.SetDebugName($"{meshName} indices", "Model");

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

            var mesh = new Mesh(
                vb,
                ib,
                header->VertexCount,
                header->IndexCount,
                header->IndexFormat,
                sphereCenter,
                sphereRadius);
            lock (_lock)
            {
                _cache[fullPath] = mesh;
            }
            return mesh;
        }
    }
}
