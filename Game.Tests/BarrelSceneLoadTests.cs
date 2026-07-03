// SPDX-License-Identifier: MIT
using System.Numerics;
using Engine.Scene;
using Engine.Scene.Components;
using Engine.Assets;
using Engine.RHI;
using Xunit;

namespace Engine.Game.Tests;

public sealed class BarrelSceneLoadTests : IDisposable
{
    /// <summary>
    /// Resolve Content/ relative to the repo root by walking up from the
    /// test assembly location until we find the Content directory.
    /// </summary>
    private static string ResolveContentRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "Content");
            if (Directory.Exists(candidate))
                return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        return "Content"; // fallback
    }

    private static readonly string ContentRoot = ResolveContentRoot();

    // ── Scene JSON parsing ──────────────────────────────────────────

    [Fact]
    public void LoadBarrelScene_HasPbrPass()
    {
        var loader = new SceneLoader(ContentRoot);
        var scene = loader.Load("barrel");

        Assert.NotEmpty(scene.Passes);
        Assert.Contains(scene.Passes, p => p.Name == "PbrPass");
    }

    [Fact]
    public void LoadBarrelScene_HasModelReferences()
    {
        var loader = new SceneLoader(ContentRoot);
        var scene = loader.Load("barrel");

        Assert.NotEmpty(scene.Models);
        var model = scene.Models[0];
        Assert.Equal("Sketchfab_model", model.Name);
        Assert.Equal("models/Sketchfab_model.mdl", model.Source);
    }

    [Fact]
    public void LoadBarrelScene_ModelPositionIsSet()
    {
        var loader = new SceneLoader(ContentRoot);
        var scene = loader.Load("barrel");

        var model = scene.Models[0];
        Assert.NotNull(model.Position);
        Assert.Equal(3, model.Position.Length);
        Assert.Equal(0, model.Position[0]);
        Assert.Equal(-1, model.Position[1]);
        Assert.Equal(0, model.Position[2]);
    }

    [Fact]
    public void LoadBarrelScene_ModelHasScaleAndRotation()
    {
        var loader = new SceneLoader(ContentRoot);
        var scene = loader.Load("barrel");

        var model = scene.Models[0];
        Assert.NotNull(model.Scale);
        Assert.Equal(1, model.Scale[0]);
        Assert.Equal(1, model.Scale[1]);
        Assert.Equal(1, model.Scale[2]);

        Assert.NotNull(model.Rotation);
        Assert.Equal(4, model.Rotation.Length);
    }

    [Fact]
    public void LoadHelloScene_HasModelsAndCameras()
    {
        var loader = new SceneLoader(ContentRoot);
        var scene = loader.Load("hello");

        Assert.NotEmpty(scene.Models);
        Assert.NotEmpty(scene.Cameras);
    }

    [Fact]
    public void LoadHelloScene_DefaultCameraHasExpectedClips()
    {
        var loader = new SceneLoader(ContentRoot);
        var scene = loader.Load("hello");

        var cam = scene.Cameras[0];
        Assert.Equal("default", cam.Name);
        Assert.Equal(0.1f, cam.Near);
        Assert.Equal(100.0f, cam.Far);
    }

    // ── Model file verification ─────────────────────────────────────

    [Fact]
    public void SketchfabMdl_FileExists()
    {
        var mdlPath = Path.Combine(ContentRoot, "models", "Sketchfab_model.mdl");
        Assert.True(File.Exists(mdlPath), $"Model file not found: {mdlPath}");
    }

    [Fact]
    public void SketchfabMdl_HasPartsAndVersion()
    {
        var mdlPath = Path.Combine(ContentRoot, "models", "Sketchfab_model.mdl");
        Assert.True(File.Exists(mdlPath));

        string json = File.ReadAllText(mdlPath);
        Assert.Contains("\"version\"", json);
        Assert.Contains("\"parts\"", json);
        Assert.Contains("Sketchfab_model_part_0.msh", json);
    }

    [Fact]
    public void SketchfabMsh_FileExists()
    {
        var mshPath = Path.Combine(ContentRoot, "models", "Sketchfab_model_part_0.msh");
        Assert.True(File.Exists(mshPath), $"Mesh file not found: {mshPath}");
    }

    [Fact]
    public void SketchfabMdl_ReferencesMaterial()
    {
        var mdlPath = Path.Combine(ContentRoot, "models", "Sketchfab_model.mdl");
        string json = File.ReadAllText(mdlPath);
        Assert.Contains("\"material\"", json);
    }

    // ── ECS world + Transform ───────────────────────────────────────

    [Fact]
    public void EcsWorld_CreateEntityAndSetTransform()
    {
        using var world = new EcsWorld();
        ulong ent = world.CreateEntity();

        var t = new Transform
        {
            Position = new Vector3(0, -1, 0),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One
        };
        world.Set(ent, t);

        Assert.True(world.TryGet<Transform>(ent, out var readBack));
        Assert.Equal(0, readBack.Position.X, 4);
        Assert.Equal(-1, readBack.Position.Y, 4);
        Assert.Equal(0, readBack.Position.Z, 4);
    }

    [Fact]
    public void EcsWorld_CreateEntityAndSetModelComponent()
    {
        using var world = new EcsWorld();
        ulong ent = world.CreateEntity();

        var mc = ModelComponent.Create(42);
        world.Set(ent, mc);

        Assert.True(world.TryGet<ModelComponent>(ent, out var readBack));
        Assert.Equal(42ul, readBack.ModelId);
    }

    [Fact]
    public void EcsWorld_EntitiesListTracksCreatedEntities()
    {
        using var world = new EcsWorld();

        ulong a = world.CreateEntity();
        ulong b = world.CreateEntity();
        ulong c = world.CreateEntity();

        Assert.Equal(3, world.Entities.Count);
        Assert.Contains(a, world.Entities);
        Assert.Contains(b, world.Entities);
        Assert.Contains(c, world.Entities);
    }

    // ── Full pipeline: scene → ECS → entity population ──────────────

    [Fact]
    public void FullPipeline_LoadScenePopulatesEcsWorld()
    {
        using var world = new EcsWorld();
        var loader = new SceneLoader(ContentRoot);
        var scene = loader.Load("barrel");

        int entityCount = 0;
        foreach (var modelRef in scene.Models)
        {
            ulong ent = world.CreateEntity();
            entityCount++;

            var pos = modelRef.Position ?? new float[] { 0, 0, 0 };
            var rot = modelRef.Rotation ?? new float[] { 0, 0, 0, 1 };
            var scl = modelRef.Scale ?? new float[] { 1, 1, 1 };

            Quaternion q = rot.Length >= 4
                ? new Quaternion(rot[0], rot[1], rot[2], rot[3])
                : Quaternion.Identity;

            world.Set(ent, new Transform
            {
                Position = new Vector3(pos[0], pos[1], pos[2]),
                Rotation = q,
                Scale = new Vector3(scl[0], scl[1], scl[2])
            });

            world.Set(ent, ModelComponent.Create((ulong)entityCount));
        }

        Assert.Equal(1, entityCount);

        var firstEnt = world.Entities[0];
        Assert.True(world.TryGet<Transform>(firstEnt, out var t));
        Assert.Equal(0, t.Position.X, 4);
        Assert.Equal(-1, t.Position.Y, 4);
        Assert.Equal(0, t.Position.Z, 4);
        Assert.Equal(1, t.Scale.X, 4);

        Assert.True(world.TryGet<ModelComponent>(firstEnt, out var mc));
        Assert.NotEqual(0ul, mc.ModelId);
    }

    // ── Render graph validation ─────────────────────────────────────

    [Fact]
    public void BarrelScene_PassesHaveValidShaderPaths()
    {
        var loader = new SceneLoader(ContentRoot);
        var scene = loader.Load("barrel");

        foreach (var pass in scene.Passes)
        {
            Assert.False(string.IsNullOrWhiteSpace(pass.Name));
            Assert.False(string.IsNullOrWhiteSpace(pass.ShaderVertex));
            Assert.False(string.IsNullOrWhiteSpace(pass.ShaderFragment));
        }
    }

    [Fact]
    public void BarrelScene_ClearColorIsValid()
    {
        var loader = new SceneLoader(ContentRoot);
        var scene = loader.Load("barrel");

        var pass = scene.Passes[0];
        Assert.NotNull(pass.ClearColor);
        Assert.Equal(4, pass.ClearColor.Length);
        foreach (var c in pass.ClearColor)
            Assert.InRange(c, 0.0, 1.0);
    }

    // ── Conditional: full RHI model load (requires native library) ──

    [Fact]
    public void FullMdlLoad_WhenNativeLibraryAvailable()
    {
        // Probe: try to create a device. Skip if native lib isn't present.
        try
        {
            using var probe = new RhiDevice();
            if (probe.Handle == IntPtr.Zero)
                return; // no device on this platform
        }
        catch (DllNotFoundException)
        {
            return; // native library not built — skip
        }
        catch (Exception ex) when (ex.Message.Contains("backend"))
        {
            return; // no RHI backend registered — skip
        }

        using var device = new RhiDevice();
        var mdlPath = Path.Combine(ContentRoot, "models", "Sketchfab_model.mdl");

        var model = ModelLoader.LoadMdl(device, mdlPath);
        Assert.NotNull(model);
        Assert.NotEmpty(model.Parts);

        foreach (var part in model.Parts)
        {
            Assert.NotEqual(0ul, part.MeshId);
        }
    }

    [Fact]
    public void FullMdlLoad_ModelPartsHaveBounds()
    {
        try
        {
            using var probe = new RhiDevice();
            if (probe.Handle == IntPtr.Zero) return;
        }
        catch (DllNotFoundException) { return; }
        catch (Exception ex) when (ex.Message.Contains("backend")) { return; }

        using var device = new RhiDevice();
        var mdlPath = Path.Combine(ContentRoot, "models", "Sketchfab_model.mdl");
        var model = ModelLoader.LoadMdl(device, mdlPath);

        foreach (var part in model.Parts)
        {
            // Bounds should be non-degenerate (min < max on at least one axis)
            var size = part.BoundsMax - part.BoundsMin;
            Assert.True(size.LengthSquared() > 0.001f,
                $"Part bounds are degenerate: min={part.BoundsMin}, max={part.BoundsMax}");
        }
    }

    public void Dispose()
    {
        // No cleanup needed
    }
}
