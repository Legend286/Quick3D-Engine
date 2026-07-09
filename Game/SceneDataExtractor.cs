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

public static class SceneDataExtractor
{
    public static unsafe void Extract(
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
        out ScenePushData pushData)
    {
        CameraData camData = default;
        camData.ViewProj = Matrix4x4.Identity;
        camData.CameraPosition = new Vector4(0, 0, 0, 1.0f); // 1.0f exposure default

        foreach (var id in world.Entities)
        {
            if (world.TryGet<Engine.Scene.Components.Camera>(id, out var cam))
            {
                var transform = world.TryGet<Transform>(id, out var t) ? t : Transform.Default;
                var view = Matrix4x4.CreateLookAt(transform.Position, transform.Position + Vector3.Transform(Vector3.UnitZ, transform.Rotation), Vector3.UnitY);
                var proj = Matrix4x4.CreatePerspectiveFieldOfView(cam.FieldOfView, aspect, cam.NearClip, cam.FarClip);
                camData.ViewProj = view * proj;
                Matrix4x4.Invert(camData.ViewProj, out Matrix4x4 invVP);
                camData.InvViewProj = invVP;
                camData.CameraPosition = new Vector4(transform.Position, 1.0f);
                break;
            }
        }

        if (camData.ViewProj == Matrix4x4.Identity)
        {
            camData.CameraPosition = new Vector4(0, 0, -5, 1.0f);
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, -5), Vector3.Zero, Vector3.UnitY);
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(60.0f * (MathF.PI / 180.0f), aspect, 0.1f, 100.0f);
            camData.ViewProj = view * proj;
            Matrix4x4.Invert(camData.ViewProj, out Matrix4x4 invVP);
            camData.InvViewProj = invVP;
        }
        
        EnsureBuffer(device, ref cameraBuffer, (ulong)sizeof(CameraData), RhiNative.BufferUsage.Storage);
        cameraBuffer.Upload(new ReadOnlySpan<CameraData>(ref camData));

        var lights = new List<LightData>();
        foreach (var l in scene.Lights)
        {
            float type = 0.0f;
            if (l.Type == "point") type = 1.0f;
            else if (l.Type == "spot") type = 2.0f;
            
            lights.Add(new LightData {
                Position = new Vector4(l.Position[0], l.Position[1], l.Position[2], l.Range),
                Direction = new Vector4(l.Direction[0], l.Direction[1], l.Direction[2], type),
                Color = new Vector4(l.Color[0], l.Color[1], l.Color[2], l.Intensity),
                SpotParams = new Vector4(l.InnerCone, l.OuterCone, 0, 0)
            });
        }
        if (lights.Count == 0)
        {
            lights.Add(new LightData {
                Position = new Vector4(0, 0, 0, 10.0f),
                Direction = new Vector4(Vector3.Normalize(new Vector3(-1, 1, -1)), 0.0f), // Dir Light
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

        foreach (var id in world.Entities)
        {
            if (world.TryGet<ModelComponent>(id, out var modelComp))
            {
                var transform = world.TryGet<Transform>(id, out var t) ? t : Transform.Default;
                var modelMatrix = Matrix4x4.CreateScale(transform.Scale) * 
                                  Matrix4x4.CreateFromQuaternion(transform.Rotation) * 
                                  Matrix4x4.CreateTranslation(transform.Position);

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
                        
                        var aabbMin = p.BoundsMin;
                        var aabbMax = p.BoundsMax;

                        instAabbMin = Vector3.Min(instAabbMin, aabbMin);
                        instAabbMax = Vector3.Max(instAabbMax, aabbMax);

                        uint matIdx = (uint)materials.Count;
                        if (material != null)
                        {
                            var sr = material.SubsurfaceRadius;
                            var sc = material.SubsurfaceColor;
                            materials.Add(new MaterialData {
                                BaseColor        = new Vector4(material.AlbedoColor[0], material.AlbedoColor[1], material.AlbedoColor[2], material.AlbedoColor[3]),
                                EmissiveColor    = new Vector4(material.EmissiveColor[0], material.EmissiveColor[1], material.EmissiveColor[2], 1.0f),
                                Metallic         = material.Metallic,
                                Roughness        = material.Roughness,
                                AlbedoTexIndex   = GetTexIndex(material.AlbedoTexture),
                                NormalTexIndex   = GetTexIndex(material.NormalTexture),
                                RmaTexIndex      = GetTexIndex(material.RmaTexture),
                                EmissiveTexIndex = 0xFFFFFFFF,
                                Subsurface       = material.Subsurface,
                                SubsurfaceRadius = new Vector4(sr.Length > 2 ? sr[0] : 1f, sr.Length > 2 ? sr[1] : 0.2f, sr.Length > 2 ? sr[2] : 0.1f, 0f),
                                SubsurfaceColor  = new Vector4(sc.Length > 2 ? sc[0] : 1f, sc.Length > 2 ? sc[1] : 1f,   sc.Length > 2 ? sc[2] : 1f,   0f),
                            });
                        }
                        else
                        {
                            materials.Add(new MaterialData { BaseColor = Vector4.One, AlbedoTexIndex = 0xFFFFFFFF, NormalTexIndex = 0xFFFFFFFF, RmaTexIndex = 0xFFFFFFFF, EmissiveTexIndex = 0xFFFFFFFF });
                        }

                        parts.Add(new PartData {
                            AabbMin = new Vector4(aabbMin, 1.0f),
                            AabbMax = new Vector4(aabbMax, 1.0f),
                            Vertices = mesh.VertexBuffer.DeviceAddress,
                            Indices = mesh.IndexBuffer.DeviceAddress,
                            IndexCount = mesh.IndexCount,
                            MaterialIdx = matIdx,
                            InstanceIdx = instIdx
                        });
                    }
                    
                    instances.Add(new InstanceData {
                        ModelMatrix = modelMatrix,
                        AabbMin = new Vector4(instAabbMin, 1.0f),
                        AabbMax = new Vector4(instAabbMax, 1.0f),
                        PartCount = (uint)model.Parts.Length,
                        FirstPartIndex = firstPart
                    });
                }
            }
        }
        
        EnsureBuffer(device, ref instanceBuffer, (ulong)instances.Count * (ulong)sizeof(InstanceData), RhiNative.BufferUsage.Storage);
        EnsureBuffer(device, ref partBuffer, (ulong)parts.Count * (ulong)sizeof(PartData), RhiNative.BufferUsage.Storage);
        EnsureBuffer(device, ref materialBuffer, (ulong)materials.Count * (ulong)sizeof(MaterialData), RhiNative.BufferUsage.Storage);
        
        if (instances.Count > 0) instanceBuffer.Upload(CollectionsMarshal.AsSpan(instances));
        if (parts.Count > 0) partBuffer.Upload(CollectionsMarshal.AsSpan(parts));
        if (materials.Count > 0) materialBuffer.Upload(CollectionsMarshal.AsSpan(materials));
        
        pushData = new ScenePushData {
            Parts = partBuffer?.DeviceAddress ?? 0,
            Instances = instanceBuffer?.DeviceAddress ?? 0,
            Materials = materialBuffer?.DeviceAddress ?? 0,
            Camera = cameraBuffer?.DeviceAddress ?? 0,
            Lights = lightBuffer?.DeviceAddress ?? 0,
            LightCount = (uint)lights.Count,
            FrameCount = 0, // Should be populated by caller
            Resolution = Vector2.Zero // Should be populated by caller
        };
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
}
