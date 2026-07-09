// SPDX-License-Identifier: MIT
using System.Numerics;
using System.Runtime.InteropServices;

namespace Engine.Game;

[StructLayout(LayoutKind.Sequential)]
public struct PartData
{
    public Vector4 AabbMin;
    public Vector4 AabbMax;
    public ulong Vertices;
    public ulong Indices;
    public uint IndexCount;
    public uint MaterialIdx;
    public uint InstanceIdx;
    public uint pad0;
}

[StructLayout(LayoutKind.Sequential)]
public struct InstanceData
{
    public Matrix4x4 ModelMatrix;
    public Vector4 AabbMin;
    public Vector4 AabbMax;
    public uint PartCount;
    public uint FirstPartIndex;
    public uint pad1;
    public uint pad2;
}

[StructLayout(LayoutKind.Sequential)]
public struct MaterialData
{
    public Vector4 BaseColor;
    public Vector4 EmissiveColor;
    public float Metallic;
    public float Roughness;
    public uint AlbedoTexIndex;
    public uint NormalTexIndex;
    public uint RmaTexIndex;
    public uint EmissiveTexIndex;
    public float Subsurface;         // 0-1: blend weight between diffuse and random-walk SSS
    public uint _pad0;
    public Vector4 SubsurfaceRadius; // xyz = per-channel mean free path (world units), w = unused
    public Vector4 SubsurfaceColor;  // xyz = scattering albedo (tints the subsurface response)
}

[StructLayout(LayoutKind.Sequential)]
public struct LightData
{
    public Vector4 Position;   // w = range
    public Vector4 Direction;  // w = type (0=Dir, 1=Point, 2=Spot)
    public Vector4 Color;      // w = intensity
    public Vector4 SpotParams; // x = innerCone, y = outerCone
}

[StructLayout(LayoutKind.Sequential)]
public struct SkyParams
{
    public Vector3 SunDirection;
    public float SunAngularRadius;   // radians, ~0.00465 for real sun
    public float SunIntensity;
    public float Turbidity;          // 2-6 typical, 2=clear
    public float GroundAlbedo;       // 0-1, typical 0.1
    public Vector3 pad_sky;
}

[StructLayout(LayoutKind.Sequential)]
public struct ScenePushData
{
    public ulong Parts;
    public ulong Instances;
    public ulong Materials;
    public ulong Camera;
    public ulong Lights;
    public uint LightCount;
    public uint FrameCount;
    public Vector2 Resolution;
    public uint DebugFlags;
    public uint hasGeometry;
    public SkyParams Sky;
}

[StructLayout(LayoutKind.Sequential)]
public struct CameraData
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 InvViewProj;
    public Vector4 CameraPosition; // w = exposure
}
