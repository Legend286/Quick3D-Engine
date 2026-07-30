// SPDX-License-Identifier: MIT
using System.Numerics;
using System.Runtime.InteropServices;

namespace Engine.Game;

[StructLayout(LayoutKind.Sequential)]
public struct PartData
{
    public Vector4 AabbMin;
    public Vector4 AabbMax;
    public Vector4 LocalOffset;
    public ulong Vertices;
    public ulong Indices;
    public uint IndexCount;
    public uint MaterialIdx;
    public uint InstanceIdx;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
public struct InstanceData
{
    public Matrix4x4 ModelMatrix;
    public Vector4 AabbMin;
    public Vector4 AabbMax;
    public uint PartCount;
    public uint FirstPartIndex;
    public uint EntityIdLow;
    public uint EntityIdHigh;
    public uint Flags;
    public uint Pad0;
    public uint Pad1;
    public uint Pad2;
}

[StructLayout(LayoutKind.Sequential)]
public struct MaterialData
{
    public Vector4 BaseColor;
    public float Metallic;
    public float Roughness;
    public uint AlbedoTexIndex;
    public uint NormalTexIndex;
    
    public Vector4 TopColor;
    public float TopMetallic;
    public float TopRoughness;
    public uint TopMaskType; // 0=None, 1=3D Noise
    public uint TopMaskTexIndex;

    public Vector4 Layer2Color;
    public float Layer2Metallic;
    public float Layer2Roughness;
    public uint Layer2MaskType;
    public uint Layer2MaskTexIndex;

    public Vector4 EmissiveColor;
    public uint EmissiveTexIndex;
    public uint RmaTexIndex;
    public float Subsurface;
    public float Clearcoat;
    
    public Vector4 SubsurfaceRadius;
    public Vector4 SubsurfaceColor;
    public float ClearcoatRoughness;
    public float NoiseScale;
    public float NoiseThresholdMin;
    public float NoiseThresholdMax;

    public float Layer2NoiseScale;
    public float Layer2NoiseThresholdMin;
    public float Layer2NoiseThresholdMax;
    public uint _pad0;
}

[StructLayout(LayoutKind.Sequential)]
public struct LightData
{
    public Vector4 Position;   // w = range
    public Vector4 Direction;  // w = type (0=Dir, 1=Point, 2=Spot)
    public Vector4 Color;      // w = intensity
    public Vector4 ShapeParams; // x = innerCone, y = outerCone, z = sourceRadius, w = flags
}

[StructLayout(LayoutKind.Sequential)]
public struct SkyParams
{
    public Vector4 SunDirAndRadius;
    public Vector4 IntensityTurbidityAlbedoPad;
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
    public Vector4 Resolution;
    public uint DebugFlags;
    public uint HasGeometry;
    public uint pad0;
    public uint pad1;
    public SkyParams Sky;
    public ulong ClusterRecords;
    public ulong ClusterLightIndices;
    public Vector4 ClusterGrid;
    public Vector4 ClusterParams;
    public Matrix4x4 DirectionalShadowViewProj;
    public Vector4 DirectionalShadowParams;
    public Matrix4x4 DirectionalShadowViewProj1;
    public Matrix4x4 DirectionalShadowViewProj2;
    public Matrix4x4 DirectionalShadowViewProj3;
    public Vector4 DirectionalShadowSplits;
    public Vector4 DirectionalShadowTextureIndices;
    public ulong PunctualShadowFaces;
    public uint PunctualShadowFaceCount;
    public uint PunctualShadowPad;
}

[StructLayout(LayoutKind.Sequential)]
public struct PunctualShadowFaceData
{
    public Matrix4x4 ViewProjection;
    public Vector4 StaticUvScaleBias;
    public Vector4 DynamicUvScaleBias;
    public Vector4 TextureIndicesAndFlags;
    public Vector4 CommittedLightPosition;
}

[StructLayout(LayoutKind.Sequential)]
public struct CameraData
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 InvViewProj;
    public Vector4 CameraPosition; // w = exposure
    public Vector4 CameraForward;
}

[StructLayout(LayoutKind.Sequential)]
public struct ClusterRecord
{
    public uint Offset;
    public uint Count;
}
