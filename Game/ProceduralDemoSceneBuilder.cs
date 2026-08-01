// SPDX-License-Identifier: MIT

using System;
using System.IO;
using System.Numerics;
using Engine.Assets;
using Engine.RHI;
using Engine.Renderer;
using Engine.Scene;
using Engine.Scene.Components;

namespace Engine.Game;

internal static class ProceduralDemoSceneBuilder
{
    public const int SubmittedTriangleCount = 1_003_808;

    public static void Build(
        RhiDevice device,
        IEntityStore world,
        string contentRoot,
        ProceduralDemoDefinition definition)
    {
        if (!definition.Enabled)
            return;

        string cacheDirectory = Path.Combine(
            contentRoot,
            ".cache",
            "procedural-demo");
        string spherePath = Path.Combine(
            cacheDirectory,
            "sphere_64x64_v1.msh");
        string torusPath = Path.Combine(
            cacheDirectory,
            "torus_64x32_v1.msh");
        string boxPath = Path.Combine(
            cacheDirectory,
            "box_v1.msh");
        string floorPath = Path.Combine(
            cacheDirectory,
            "floor_100x100_v1.msh");

        if (!File.Exists(spherePath))
            PrimitiveMeshFactory.GenerateUVSphere(spherePath, 64, 64);
        if (!File.Exists(torusPath))
            PrimitiveMeshFactory.GenerateTorus(torusPath, 64, 32);
        if (!File.Exists(boxPath))
            PrimitiveMeshFactory.GenerateBox(boxPath);
        if (!File.Exists(floorPath))
        {
            PrimitiveMeshFactory.GeneratePlane(
                floorPath,
                80.0f,
                80.0f,
                100,
                100);
        }

        Mesh sphere = MeshLoader.LoadMsh(device, spherePath);
        Mesh torus = MeshLoader.LoadMsh(device, torusPath);
        Mesh box = MeshLoader.LoadMsh(device, boxPath);
        Mesh floor = MeshLoader.LoadMsh(device, floorPath);
        Material[] materials = CreateMaterials();
        ulong[] materialIds = new ulong[materials.Length];
        for (int i = 0; i < materials.Length; ++i)
            materialIds[i] = AssetRegistry.RegisterMaterial(materials[i]);

        ulong[,] modelIds = new ulong[3, materials.Length];
        for (int materialIndex = 0;
             materialIndex < materials.Length;
             ++materialIndex)
        {
            modelIds[0, materialIndex] = CreateModel(
                sphere,
                materials[materialIndex],
                materialIds[materialIndex],
                new Vector3(-1.0f),
                new Vector3(1.0f));
            modelIds[1, materialIndex] = CreateModel(
                torus,
                materials[materialIndex],
                materialIds[materialIndex],
                new Vector3(-1.35f, -0.35f, -1.35f),
                new Vector3(1.35f, 0.35f, 1.35f));
            modelIds[2, materialIndex] = CreateModel(
                box,
                materials[materialIndex],
                materialIds[materialIndex],
                new Vector3(-0.5f),
                new Vector3(0.5f));
        }
        ulong floorModel = CreateModel(
            floor,
            materials[2],
            materialIds[2],
            new Vector3(-40.0f, -0.01f, -40.0f),
            new Vector3(40.0f, 0.01f, 40.0f));

        CreateModelEntity(
            world,
            floorModel,
            new Vector3(0.0f, 0.0f, 18.0f),
            Quaternion.Identity,
            Vector3.One);

        for (int row = 0; row < 8; ++row)
        {
            for (int column = 0; column < 12; ++column)
            {
                int index = row * 12 + column;
                float scale = 1.05f + (index % 5) * 0.12f;
                CreateModelEntity(
                    world,
                    modelIds[0, index % materials.Length],
                    new Vector3(
                        (column - 5.5f) * 6.0f,
                        2.0f + (index % 3) * 0.35f,
                        (row - 1.5f) * 6.0f),
                    Quaternion.CreateFromYawPitchRoll(
                        index * 0.31f,
                        index * 0.07f,
                        0.0f),
                    new Vector3(
                        scale,
                        scale * (0.8f + (index % 4) * 0.12f),
                        scale));
            }
        }

        for (int row = 0; row < 6; ++row)
        {
            for (int column = 0; column < 8; ++column)
            {
                int index = row * 8 + column;
                CreateModelEntity(
                    world,
                    modelIds[1, (index + 3) % materials.Length],
                    new Vector3(
                        (column - 3.5f) * 9.0f,
                        5.8f + (index % 4) * 0.4f,
                        (row - 0.5f) * 9.0f),
                    Quaternion.CreateFromYawPitchRoll(
                        index * 0.41f,
                        0.25f + (index % 3) * 0.22f,
                        index * 0.13f),
                    Vector3.One * (1.2f + (index % 3) * 0.18f));
            }
        }

        for (int row = 0; row < 8; ++row)
        {
            for (int column = 0; column < 8; ++column)
            {
                int index = row * 8 + column;
                Vector3 position = new(
                    (column - 3.5f) * 8.0f,
                    0.75f + (index % 3) * 0.35f,
                    (row - 1.5f) * 8.0f);
                bool moving = definition.AnimateObjects &&
                    index < Math.Clamp(
                        definition.MovingObjectCount,
                        0,
                        64);
                ulong entity = CreateModelEntity(
                    world,
                    modelIds[2, (index + 5) % materials.Length],
                    position,
                    Quaternion.CreateFromYawPitchRoll(
                        index * 0.19f,
                        index * 0.11f,
                        index * 0.05f),
                    new Vector3(
                        1.4f + (index % 4) * 0.35f,
                        1.2f + (index % 5) * 0.45f,
                        1.4f + ((index + 2) % 4) * 0.35f),
                    staticShadowCaster: !moving);
                if (moving)
                {
                    world.Set(entity, new OscillatingModelComponent
                    {
                        Origin = position,
                        Axis = index % 2 == 0
                            ? Vector3.UnitY
                            : Vector3.Normalize(
                                new Vector3(0.4f, 1.0f, 0.2f)),
                        Amplitude = 1.0f + (index % 4) * 0.35f,
                        Frequency = 0.45f + (index % 5) * 0.08f,
                        Phase = index * 0.67f,
                    });
                }
            }
        }

        CreateLights(world, definition);
        Engine.CBindings.Log.Info(
            $"[ProceduralDemo] Built {SubmittedTriangleCount:N0} " +
            "submitted triangles",
            "Game");
    }

    private static ulong CreateModel(
        Mesh mesh,
        Material material,
        ulong materialId,
        Vector3 boundsMin,
        Vector3 boundsMax)
    {
        ulong meshId = AssetRegistry.RegisterMesh(mesh);
        var model = new Model
        {
            Parts =
            [
                new ModelPart
                {
                    Mesh = mesh,
                    MeshId = meshId,
                    Material = material,
                    MaterialId = materialId,
                    BoundsMin = boundsMin,
                    BoundsMax = boundsMax,
                },
            ],
        };
        return AssetRegistry.RegisterModel(model);
    }

    private static ulong CreateModelEntity(
        IEntityStore world,
        ulong modelId,
        Vector3 position,
        Quaternion rotation,
        Vector3 scale,
        bool staticShadowCaster = true)
    {
        ulong entity = world.CreateEntity();
        world.Set(
            entity,
            ModelComponent.Create(modelId, staticShadowCaster));
        world.Set(entity, new Transform
        {
            Position = position,
            Rotation = rotation,
            Scale = scale,
        });
        MarkProcedural(world, entity);
        return entity;
    }

    private static void CreateLights(
        IEntityStore world,
        ProceduralDemoDefinition definition)
    {
        int pointLightCount = Math.Clamp(
            definition.PointLightCount,
            0,
            128);
        int spotLightCount = Math.Clamp(
            definition.SpotLightCount,
            0,
            64);
        int animatedPointLightCount = definition.AnimateLights
            ? Math.Clamp(
                definition.AnimatedPointLightCount,
                0,
                pointLightCount)
            : 0;
        int animatedSpotLightCount = definition.AnimateLights
            ? Math.Clamp(
                definition.AnimatedSpotLightCount,
                0,
                spotLightCount)
            : 0;
        for (int index = 0; index < pointLightCount; ++index)
        {
            float phase =
                2.0f * MathF.PI * index /
                Math.Max(pointLightCount, 1);
            float radius = 10.0f + (index % 5) * 6.0f;
            var orbit = new OrbitingLightComponent
            {
                Center = new Vector3(0.0f, 2.5f, 18.0f),
                Radius = radius,
                AngularSpeed =
                    (index % 2 == 0 ? 1.0f : -1.0f) *
                    (0.12f + (index % 7) * 0.018f),
                Phase = phase,
                VerticalAmplitude = 2.0f + (index % 4) * 0.7f,
                VerticalFrequency = 0.45f + (index % 5) * 0.08f,
                OrbitHeight = 5.0f + (index % 3) * 2.2f,
            };
            Vector3 position = EvaluateOrbit(orbit, 0.0f);
            ulong entity = world.CreateEntity();
            world.Set(entity, new Transform
            {
                Position = position,
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
            });
            world.Set(entity, new PointLightComponent
            {
                Color = HueToRgb(index / (float)Math.Max(pointLightCount, 1)),
                Intensity = 76.0f + (index % 4) * 14.0f,
                Range = 26.0f + (index % 3) * 5.0f,
                SourceRadius = 0.18f + (index % 3) * 0.06f,
                CastShadows = true,
            });
            if (index < animatedPointLightCount)
                world.Set(entity, orbit);
            MarkProcedural(world, entity);
        }

        for (int index = 0; index < spotLightCount; ++index)
        {
            float phase =
                2.0f * MathF.PI * index /
                Math.Max(spotLightCount, 1);
            var orbit = new OrbitingLightComponent
            {
                Center = new Vector3(0.0f, 2.0f, 18.0f),
                Radius = 24.0f + (index % 3) * 5.0f,
                AngularSpeed =
                    (index % 2 == 0 ? 0.10f : -0.13f),
                Phase = phase,
                VerticalAmplitude = 2.0f,
                VerticalFrequency = 0.35f + index * 0.025f,
                OrbitHeight = 13.0f + (index % 2) * 3.0f,
                AimAtCenter = true,
            };
            Vector3 position = EvaluateOrbit(orbit, 0.0f);
            Vector3 direction = Vector3.Normalize(orbit.Center - position);
            ulong entity = world.CreateEntity();
            world.Set(entity, new Transform
            {
                Position = position,
                Rotation = LightMath.GetSpotRotation(direction),
                Scale = Vector3.One,
            });
            world.Set(entity, new SpotLightComponent
            {
                Color = HueToRgb((index + 0.5f) / Math.Max(spotLightCount, 1)),
                Intensity = 150.0f,
                Range = 68.0f,
                Direction = direction,
                InnerCone = 0.91f,
                OuterCone = 0.75f,
                SourceRadius = 0.12f,
                CastShadows = true,
            });
            if (index < animatedSpotLightCount)
                world.Set(entity, orbit);
            MarkProcedural(world, entity);
        }
    }

    internal static Vector3 EvaluateOrbit(
        OrbitingLightComponent orbit,
        float time)
    {
        float angle = orbit.Phase + time * orbit.AngularSpeed;
        return orbit.Center + new Vector3(
            MathF.Cos(angle) * orbit.Radius,
            orbit.OrbitHeight +
                MathF.Sin(
                    orbit.Phase +
                    time * orbit.VerticalFrequency) *
                orbit.VerticalAmplitude,
            MathF.Sin(angle) * orbit.Radius);
    }

    private static void MarkProcedural(
        IEntityStore world,
        ulong entity)
    {
        world.Set(
            entity,
            new ProceduralDemoEntityComponent { Value = 1 });
    }

    private static Vector3 HueToRgb(float hue)
    {
        float wrapped = hue - MathF.Floor(hue);
        float x = wrapped * 6.0f;
        int segment = (int)x;
        float fraction = x - segment;
        return segment switch
        {
            0 => new Vector3(1.0f, fraction, 0.08f),
            1 => new Vector3(1.0f - fraction, 1.0f, 0.08f),
            2 => new Vector3(0.08f, 1.0f, fraction),
            3 => new Vector3(0.08f, 1.0f - fraction, 1.0f),
            4 => new Vector3(fraction, 0.08f, 1.0f),
            _ => new Vector3(1.0f, 0.08f, 1.0f - fraction),
        };
    }

    private static Material[] CreateMaterials()
    {
        return
        [
            CreateMaterial(
                new[] { 0.75f, 0.055f, 0.035f, 1.0f },
                0.0f,
                0.22f,
                0.65f),
            CreateMaterial(
                new[] { 1.0f, 0.58f, 0.12f, 1.0f },
                0.92f,
                0.24f,
                0.2f),
            CreateMaterial(
                new[] { 0.035f, 0.24f, 0.21f, 1.0f },
                0.0f,
                0.82f,
                0.0f),
            CreateMaterial(
                new[] { 0.025f, 0.11f, 0.62f, 1.0f },
                0.12f,
                0.16f,
                0.9f),
            CreateMaterial(
                new[] { 0.87f, 0.8f, 0.64f, 1.0f },
                0.0f,
                0.48f,
                0.35f,
                0.28f),
            CreateMaterial(
                new[] { 0.48f, 0.15f, 0.055f, 1.0f },
                0.88f,
                0.34f,
                0.25f),
            CreateMaterial(
                new[] { 0.22f, 0.025f, 0.34f, 1.0f },
                0.0f,
                0.56f,
                0.5f),
            CreateMaterial(
                new[] { 0.32f, 0.36f, 0.42f, 1.0f },
                0.68f,
                0.62f,
                0.0f),
        ];
    }

    private static Material CreateMaterial(
        float[] color,
        float metallic,
        float roughness,
        float clearcoat,
        float subsurface = 0.0f)
    {
        return new Material
        {
            AlbedoColor = color,
            Metallic = metallic,
            Roughness = roughness,
            Clearcoat = clearcoat,
            ClearcoatRoughness = 0.18f,
            Subsurface = subsurface,
            SubsurfaceColor = color[..3],
        };
    }
}
