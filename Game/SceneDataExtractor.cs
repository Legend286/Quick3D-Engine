// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.Assets;
using Engine.RHI;
using Engine.Scene;
using Engine.Scene.Components;
using Engine.CBindings;

namespace Engine.Game;

internal sealed class SceneFrameData
{
    public CameraData Camera;
    public Vector3 SkySunDir;
    public float SkySunRadius;
    public List<LightData> Lights { get; } = new();
    public List<InstanceData> Instances { get; } = new();
    public List<PartData> Parts { get; } = new();
    public List<MaterialData> Materials { get; } = new();
    public HashSet<Engine.Assets.Mesh> UniqueMeshes { get; } = new();
    public ScenePushData PushData;
}

internal static class SceneDataExtractor
{
    internal static unsafe void Extract(
        RhiDevice device,
        IEntityStore world,
        SceneGraph scene,
        RhiBindlessHeap bindlessHeap,
        float aspect,
        ref RhiBuffer cameraBuffer,
        ref RhiBuffer lightBuffer,
        ref RhiBuffer instanceBuffer,
        ref RhiBuffer partBuffer,
        ref RhiBuffer materialBuffer,
        ulong activeCameraId,
        Vector3 localCameraForward,
        uint frameCount,
        uint debugFlags,
        uint width,
        uint height,
        out SceneFrameData frameData,
        out ScenePushData pushData)
    {
        frameData = new SceneFrameData();
        CameraData camData = default;
        camData.ViewProj = Matrix4x4.Identity;
        camData.CameraPosition = new Vector4(0, 0, 0, 1.0f);

        if (world.TryGet<Engine.Scene.Components.Camera>(activeCameraId, out var cam))
        {
            var transform = world.TryGet<Transform>(activeCameraId, out var t) ? t : Transform.Default;
            var forward = Vector3.Transform(localCameraForward, transform.Rotation);
            var view = Matrix4x4.CreateLookAt(transform.Position, transform.Position + forward, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(cam.FieldOfView, aspect, cam.NearClip, cam.FarClip);
            camData.ViewProj = view * proj;
            Matrix4x4.Invert(camData.ViewProj, out Matrix4x4 invVP);
            camData.InvViewProj = invVP;
            camData.CameraPosition = new Vector4(transform.Position, 1.0f);
            camData.CameraForward = new Vector4(forward, 0.0f);
        }

        if (camData.ViewProj == Matrix4x4.Identity)
        {
            camData.CameraPosition = new Vector4(0, 0, -5, 1.0f);
            var fallbackForward = Vector3.UnitZ;
            camData.CameraForward = new Vector4(fallbackForward, 0.0f);
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, -5), new Vector3(0, 0, -5) + fallbackForward, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(60.0f * (MathF.PI / 180.0f), aspect, 0.1f, 100.0f);
            camData.ViewProj = view * proj;
            Matrix4x4.Invert(camData.ViewProj, out Matrix4x4 invVP);
            camData.InvViewProj = invVP;
        }

        EnsureBuffer(device, ref cameraBuffer, (ulong)sizeof(CameraData), RhiNative.BufferUsage.Storage);
        cameraBuffer.Upload(new ReadOnlySpan<CameraData>(ref camData));

        var lights = new List<LightData>();
        Vector3 skySunDir = Vector3.Normalize(new Vector3(0.5f, 1.0f, 0.5f));
        float skySunRadius = 0.00465f;

        foreach (var l in scene.Lights)
        {
            float type = 0.0f;
            float p1 = l.InnerCone;
            float p2 = l.OuterCone;
            if (l.Type == "point") type = 1.0f;
            else if (l.Type == "spot") type = 2.0f;
            else if (l.Type == "directional")
            {
                type = 0.0f;
                p1 = l.SunRadius;
                skySunDir = Vector3.Normalize(new Vector3(-l.Direction[0], -l.Direction[1], -l.Direction[2]));
                skySunRadius = l.SunRadius;
            }

            lights.Add(new LightData
            {
                Position = new Vector4(l.Position[0], l.Position[1], l.Position[2], l.Range),
                Direction = new Vector4(l.Direction[0], l.Direction[1], l.Direction[2], type),
                Color = new Vector4(l.Color[0], l.Color[1], l.Color[2], l.Intensity),
                SpotParams = new Vector4(p1, p2, 0, 0)
            });
        }
        if (lights.Count == 0)
        {
            lights.Add(new LightData
            {
                Position = new Vector4(0, 0, 0, 10.0f),
                Direction = new Vector4(Vector3.Normalize(new Vector3(-1, 1, -1)), 0.0f),
                Color = new Vector4(1, 1, 1, 2.0f),
                SpotParams = Vector4.Zero
            });
        }

        EnsureBuffer(device, ref lightBuffer, (ulong)lights.Count * (ulong)sizeof(LightData), RhiNative.BufferUsage.Storage);
        lightBuffer.Upload(CollectionsMarshal.AsSpan(lights));

        var instances = new List<InstanceData>();
        var parts = new List<PartData>();
        var materials = new List<MaterialData>();

        uint GetTexIndex(RhiTexture? tex)
        {
            if (tex == null) return 0xFFFFFFFF;
            if (bindlessHeap.TryLookup(tex, out uint idx)) return idx;
            return bindlessHeap.Register(tex);
        }

        var sortedEntities = new List<ulong>(world.Entities);
        sortedEntities.Sort();

        foreach (var id in sortedEntities)
        {
            if (world.TryGet<ModelComponent>(id, out var modelComp))
            {
                var transform = world.TryGet<Transform>(id, out var t) ? t : Transform.Default;

                Vector3 s = transform.Scale;
                if (float.IsNaN(s.X) || float.IsInfinity(s.X) || MathF.Abs(s.X) < 1e-5f) s.X = 1f;
                if (float.IsNaN(s.Y) || float.IsInfinity(s.Y) || MathF.Abs(s.Y) < 1e-5f) s.Y = 1f;
                if (float.IsNaN(s.Z) || float.IsInfinity(s.Z) || MathF.Abs(s.Z) < 1e-5f) s.Z = 1f;

                Vector3 pPos = transform.Position;
                if (float.IsNaN(pPos.X) || float.IsInfinity(pPos.X)) pPos.X = 0f;
                if (float.IsNaN(pPos.Y) || float.IsInfinity(pPos.Y)) pPos.Y = 0f;
                if (float.IsNaN(pPos.Z) || float.IsInfinity(pPos.Z)) pPos.Z = 0f;

                Quaternion q = transform.Rotation;
                if (float.IsNaN(q.X) || float.IsNaN(q.Y) || float.IsNaN(q.Z) || float.IsNaN(q.W) ||
                    float.IsInfinity(q.X) || float.IsInfinity(q.Y) || float.IsInfinity(q.Z) || float.IsInfinity(q.W) ||
                    q.LengthSquared() < 1e-6f)
                {
                    q = Quaternion.Identity;
                }
                else
                {
                    q = Quaternion.Normalize(q);
                }

                var modelMatrix = Matrix4x4.CreateScale(s) *
                                  Matrix4x4.CreateFromQuaternion(q) *
                                  Matrix4x4.CreateTranslation(pPos);

                if (float.IsNaN(modelMatrix.M11) || float.IsNaN(modelMatrix.M41) ||
                    float.IsInfinity(modelMatrix.M11) || float.IsInfinity(modelMatrix.M41))
                {
                    modelMatrix = Matrix4x4.Identity;
                }

                var model = AssetRegistry.GetModel(modelComp.ModelId);
                if (model != null && model.Parts != null)
                {
                    uint instIdx = (uint)instances.Count;
                    uint firstPart = (uint)parts.Count;

                    Vector3 instAabbMin = new Vector3(float.MaxValue);
                    Vector3 instAabbMax = new Vector3(float.MinValue);

                    foreach (var p in model.Parts)
                    {
                        var mesh = AssetRegistry.GetMesh(p.MeshId);
                        var material = AssetRegistry.GetMaterial(p.MaterialId);

                        if (mesh == null) continue;
                        frameData.UniqueMeshes.Add(mesh);

                        var aabbMin = p.BoundsMin;
                        var aabbMax = p.BoundsMax;

                        instAabbMin = Vector3.Min(instAabbMin, aabbMin);
                        instAabbMax = Vector3.Max(instAabbMax, aabbMax);

                        uint matIdx = (uint)materials.Count;
                        if (material != null)
                        {
                            materials.Add(new MaterialData
                            {
                                BaseColor = ReadColor(material.AlbedoColor, Vector4.One),
                                EmissiveColor = ReadColor(material.EmissiveColor, new Vector4(0, 0, 0, 1)),
                                Metallic = material.Metallic,
                                Roughness = material.Roughness,
                                AlbedoTexIndex = GetTexIndex(material.AlbedoTexture),
                                NormalTexIndex = GetTexIndex(material.NormalTexture),
                                RmaTexIndex = GetTexIndex(material.RmaTexture),
                                EmissiveTexIndex = 0xFFFFFFFF,
                                Subsurface = material.Subsurface,
                                SubsurfaceRadius = ReadVector3(material.SubsurfaceRadius, new Vector3(1.0f, 0.2f, 0.1f)),
                                SubsurfaceColor = ReadVector3(material.SubsurfaceColor, Vector3.One),
                                TopColor = ReadColor(material.TopColor, Vector4.One),
                                TopMetallic = material.TopMetallic,
                                TopRoughness = material.TopRoughness,
                                TopMaskType = material.TopMaskType,
                                TopMaskTexIndex = GetTexIndex(material.TopMaskTexture),
                                Layer2Color = ReadColor(material.Layer2Color, Vector4.One),
                                Layer2Metallic = material.Layer2Metallic,
                                Layer2Roughness = material.Layer2Roughness,
                                Layer2MaskType = material.Layer2MaskType,
                                Layer2MaskTexIndex = GetTexIndex(material.Layer2MaskTexture),
                                Clearcoat = material.Clearcoat,
                                ClearcoatRoughness = material.ClearcoatRoughness,
                                NoiseScale = material.NoiseScale,
                                NoiseThresholdMin = material.NoiseThresholdMin,
                                NoiseThresholdMax = material.NoiseThresholdMax,
                                Layer2NoiseScale = material.Layer2NoiseScale,
                                Layer2NoiseThresholdMin = material.Layer2NoiseThresholdMin,
                                Layer2NoiseThresholdMax = material.Layer2NoiseThresholdMax
                            });
                        }
                        else
                        {
                            materials.Add(new MaterialData { BaseColor = Vector4.One, AlbedoTexIndex = 0xFFFFFFFF, NormalTexIndex = 0xFFFFFFFF, RmaTexIndex = 0xFFFFFFFF, EmissiveTexIndex = 0xFFFFFFFF });
                        }

                        parts.Add(new PartData
                        {
                            AabbMin = new Vector4(aabbMin, 1.0f),
                            AabbMax = new Vector4(aabbMax, 1.0f),
                            Vertices = mesh.VertexBuffer.DeviceAddress,
                            Indices = mesh.IndexBuffer.DeviceAddress,
                            IndexCount = mesh.IndexCount,
                            MaterialIdx = matIdx,
                            InstanceIdx = instIdx,
                            Flags = mesh.IndexFormat == 32 ? 1u : 0u
                        });
                    }

                    if (parts.Count > firstPart)
                    {
                        instances.Add(new InstanceData
                        {
                            ModelMatrix = modelMatrix,
                            AabbMin = new Vector4(instAabbMin, 1.0f),
                            AabbMax = new Vector4(instAabbMax, 1.0f),
                            PartCount = (uint)(parts.Count - firstPart),
                            FirstPartIndex = firstPart,
                            EntityIdLow = (uint)(id & 0xFFFFFFFF),
                            EntityIdHigh = (uint)(id >> 32)
                        });
                    }
                }
            }
        }

        EnsureBuffer(device, ref instanceBuffer, (ulong)instances.Count * (ulong)sizeof(InstanceData), RhiNative.BufferUsage.Storage);
        EnsureBuffer(device, ref partBuffer, (ulong)parts.Count * (ulong)sizeof(PartData), RhiNative.BufferUsage.Storage);
        EnsureBuffer(device, ref materialBuffer, (ulong)materials.Count * (ulong)sizeof(MaterialData), RhiNative.BufferUsage.Storage);

        if (instances.Count > 0) instanceBuffer.Upload(CollectionsMarshal.AsSpan(instances));
        if (parts.Count > 0) partBuffer.Upload(CollectionsMarshal.AsSpan(parts));
        if (materials.Count > 0) materialBuffer.Upload(CollectionsMarshal.AsSpan(materials));

        frameData.Camera = camData;
        frameData.SkySunDir = skySunDir;
        frameData.SkySunRadius = skySunRadius;
        frameData.Lights.AddRange(lights);
        frameData.Instances.AddRange(instances);
        frameData.Parts.AddRange(parts);
        frameData.Materials.AddRange(materials);

        pushData = new ScenePushData
        {
            Parts = partBuffer?.DeviceAddress ?? 0,
            Instances = instanceBuffer?.DeviceAddress ?? 0,
            Materials = materialBuffer?.DeviceAddress ?? 0,
            Camera = cameraBuffer?.DeviceAddress ?? 0,
            Lights = lightBuffer?.DeviceAddress ?? 0,
            LightCount = (uint)lights.Count,
            FrameCount = frameCount,
            Resolution = new Vector4(width, height, width > 0 ? 1.0f / width : 0.0f, height > 0 ? 1.0f / height : 0.0f),
            DebugFlags = debugFlags,
            HasGeometry = instances.Count > 0 ? 1u : 0u,
            pad0 = 0,
            pad1 = 0,
            Sky = new SkyParams
            {
                SunDirAndRadius = new Vector4(skySunDir, skySunRadius),
                IntensityTurbidityAlbedoPad = new Vector4(1.0f, 2.0f, 0.1f, 0.0f)
            }
        };
        frameData.PushData = pushData;
    }

    private static void EnsureBuffer(RhiDevice device, ref RhiBuffer buffer, ulong requiredSize, RhiNative.BufferUsage usage)
    {
        if (requiredSize == 0) requiredSize = 16;
        if (buffer == null || buffer.Size < requiredSize)
        {
            buffer?.Dispose();
            ulong newSize = Math.Max(requiredSize, buffer == null ? requiredSize : buffer.Size * 2);
            buffer = RhiBuffer.Create(device, newSize, usage);
        }
    }

    private static Vector4 ReadColor(float[]? values, Vector4 fallback)
    {
        if (values == null || values.Length == 0) return fallback;
        return new Vector4(
            values.Length > 0 ? values[0] : fallback.X,
            values.Length > 1 ? values[1] : fallback.Y,
            values.Length > 2 ? values[2] : fallback.Z,
            values.Length > 3 ? values[3] : fallback.W);
    }

    private static Vector4 ReadVector3(float[]? values, Vector3 fallback)
    {
        if (values == null || values.Length == 0) return new Vector4(fallback, 0.0f);
        return new Vector4(
            values.Length > 0 ? values[0] : fallback.X,
            values.Length > 1 ? values[1] : fallback.Y,
            values.Length > 2 ? values[2] : fallback.Z,
            0.0f);
    }
}
