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
    public RhiBuffer? SkinSourceBuffer;
    public uint VertexCount;
    public uint IndexCount;
    public uint IndexFormat; // 16 or 32
    public MeshDeformationKind DeformationKind;
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
        float boundsSphereRadius,
        RhiBuffer? skinSourceBuffer = null,
        MeshDeformationKind deformationKind = MeshDeformationKind.Static)
    {
        VertexBuffer = vb;
        IndexBuffer = ib;
        SkinSourceBuffer = skinSourceBuffer;
        VertexCount = vc;
        IndexCount = ic;
        IndexFormat = ifmt;
        DeformationKind = deformationKind;
        BoundsSphereCenter = boundsSphereCenter;
        BoundsSphereRadius = boundsSphereRadius;
    }

    public void Dispose()
    {
        VertexBuffer?.Dispose();
        IndexBuffer?.Dispose();
        SkinSourceBuffer?.Dispose();
        Blas?.Dispose();
    }
}

public static class MeshLoader
{
    private const uint StaticMeshMagic = 0x3148534D;
    private const uint LegacySkinnedMeshMagic = 0x3248534D;
    private const uint IntegerSkinnedMeshMagic = 0x3348534D;

    [StructLayout(LayoutKind.Sequential)]
    private struct MeshHeader
    {
        public uint Magic;
        public uint VertexCount;
        public uint IndexCount;
        public uint IndexFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LegacySkinSourceVertexGpu
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 Texcoord;
        public Vector4 Tangent;
        public Vector4 BoneIndices;
        public Vector4 BoneWeights;
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
            bool isLegacySkinned = header->Magic == LegacySkinnedMeshMagic;
            bool isSkinned = isLegacySkinned ||
                header->Magic == IntegerSkinnedMeshMagic;
            if (!isSkinned && header->Magic != StaticMeshMagic)
                throw new InvalidDataException("Invalid .msh file magic.");
            if (header->VertexCount == 0 ||
                header->IndexFormat is not (16u or 32u))
                throw new InvalidDataException("Invalid .msh counts or index format.");

            ulong iSize = (ulong)header->IndexCount * (header->IndexFormat == 16 ? 2ul : 4ul);
            if ((ulong)fileBytes.Length < 16ul + iSize)
                throw new InvalidDataException("Truncated .msh index data.");
            ulong expectedVSize = (ulong)fileBytes.Length - 16ul - iSize;
            int stride = checked((int)(expectedVSize / header->VertexCount));
            (Vector3 sphereCenter, float sphereRadius) =
                CalculateBoundingSphere(
                    ptr + 16,
                    header->VertexCount,
                    stride);

            ulong vSizeTarget = (ulong)header->VertexCount * (ulong)sizeof(Vertex);
            ulong skinSourceSize = isSkinned
                ? checked((ulong)header->VertexCount * (ulong)Marshal.SizeOf<SkinSourceVertexGpu>())
                : 0ul;

            RhiBuffer vb = RhiBuffer.Create(device, vSizeTarget, RhiNative.BufferUsage.Vertex | RhiNative.BufferUsage.Storage);
            RhiBuffer? skinSourceBuffer = isSkinned
                ? RhiBuffer.Create(device, skinSourceSize, RhiNative.BufferUsage.Storage)
                : null;
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
            else if (stride == sizeof(Vertex) && !isSkinned)
            {
                vb.Upload(new IntPtr(ptr + 16), vSizeTarget);
            }
            else if (isSkinned &&
                     stride == Marshal.SizeOf<SkinSourceVertexGpu>())
            {
                SkinSourceVertexGpu[] bindPose =
                    new SkinSourceVertexGpu[header->VertexCount];
                if (!isLegacySkinned)
                {
                    skinSourceBuffer!.Upload(new IntPtr(ptr + 16), skinSourceSize);
                    new ReadOnlySpan<byte>(ptr + 16, checked((int)skinSourceSize))
                        .CopyTo(MemoryMarshal.AsBytes(bindPose.AsSpan()));
                }
                else
                {
                    LegacySkinSourceVertexGpu[] legacy =
                        new LegacySkinSourceVertexGpu[header->VertexCount];
                    new ReadOnlySpan<byte>(ptr + 16, checked((int)skinSourceSize))
                        .CopyTo(MemoryMarshal.AsBytes(legacy.AsSpan()));
                    for (int index = 0; index < legacy.Length; ++index)
                    {
                        LegacySkinSourceVertexGpu source = legacy[index];
                        Vector4 indices = source.BoneIndices;
                        if (!float.IsFinite(indices.X) ||
                            !float.IsFinite(indices.Y) ||
                            !float.IsFinite(indices.Z) ||
                            !float.IsFinite(indices.W) ||
                            indices.X < 0.0f || indices.Y < 0.0f ||
                            indices.Z < 0.0f || indices.W < 0.0f ||
                            indices.X != MathF.Truncate(indices.X) ||
                            indices.Y != MathF.Truncate(indices.Y) ||
                            indices.Z != MathF.Truncate(indices.Z) ||
                            indices.W != MathF.Truncate(indices.W))
                        {
                            skinSourceBuffer?.Dispose();
                            vb.Dispose();
                            ib.Dispose();
                            throw new InvalidDataException(
                                "Legacy MSH2 contains a non-integer joint index.");
                        }
                        bindPose[index] = new SkinSourceVertexGpu
                        {
                            Position = source.Position,
                            Normal = source.Normal,
                            Texcoord = source.Texcoord,
                            Tangent = source.Tangent,
                            BoneIndices = new GpuUInt4(
                                checked((uint)indices.X),
                                checked((uint)indices.Y),
                                checked((uint)indices.Z),
                                checked((uint)indices.W)),
                            BoneWeights = source.BoneWeights,
                        };
                    }
                    skinSourceBuffer!.Upload<SkinSourceVertexGpu>(bindPose.AsSpan());
                }
                Vertex[] output = new Vertex[header->VertexCount];
                for (int index = 0; index < output.Length; ++index)
                {
                    SkinSourceVertexGpu source = bindPose[index];
                    output[index] = new Vertex
                    {
                        px = source.Position.X,
                        py = source.Position.Y,
                        pz = source.Position.Z,
                        nx = source.Normal.X,
                        ny = source.Normal.Y,
                        nz = source.Normal.Z,
                        tu = source.Texcoord.X,
                        tv = source.Texcoord.Y,
                        tx = source.Tangent.X,
                        ty = source.Tangent.Y,
                        tz = source.Tangent.Z,
                        tw = source.Tangent.W,
                    };
                }
                vb.Upload<Vertex>(output.AsSpan());
            }
            else
            {
                skinSourceBuffer?.Dispose();
                vb.Dispose();
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
                sphereRadius,
                skinSourceBuffer,
                isSkinned
                    ? MeshDeformationKind.Deforming
                    : MeshDeformationKind.Static);
            lock (_lock)
            {
                _cache[fullPath] = mesh;
            }
            return mesh;
        }
    }
}
