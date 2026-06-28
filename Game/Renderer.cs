// SPDX-License-Identifier: MIT
// Renderer orchestration: takes a Scene, builds the pass list, compiles the
// render graph, drives the executor each frame.
//
// World ownership: the renderer does NOT own the EcsWorld; the caller hands
// it in at construction. This keeps the renderer free of GC pressure and
// lets the editor share a single world across multiple viewports later.

using System;
using System.Collections.Generic;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.Scene;
using static Engine.CBindings.Log;

namespace Engine.Game;

public sealed class Renderer : IDisposable
{
    private readonly RhiDevice _device;
    private readonly RhiSwapchain _swap;
    private readonly IEntityStore _world;
    private SceneLoader? _loader;
    private string _contentRoot = "Content";

    private RenderPlan? _plan;

    /// <summary>Sentinel handle on which the executor binds the swapchain
    /// back-buffer before each frame. Console-friendly constant so callers
    /// can reference the same handle.</summary>
    public static readonly ResourceHandle BackBufferHandle = new(0x80000000);

    public Renderer(RhiDevice device, RhiSwapchain swap, IEntityStore world)
    {
        _device = device;
        _swap = swap;
        _world = world;
    }

    public IEntityStore World => _world;

    public void LoadScene(string contentRoot, string sceneName)
    {
        _contentRoot = contentRoot;
        _loader = new SceneLoader(contentRoot);
        SceneGraph scene = _loader.Load(sceneName);

        // Seed or update the MeshComponent entity in the ECS world using scene graph vertices
        ulong? existingEnt = null;
        for (ulong id = 1; id < 1024; ++id)
        {
            if (_world.TryGet<MeshComponent>(id, out _))
            {
                existingEnt = id;
                break;
            }
        }
        ulong ent = existingEnt ?? _world.CreateEntity();

        MeshRef? meshRef = null;
        foreach (var m in scene.Meshes)
        {
            if (m.Vertices != null && m.Vertices.Count > 0)
            {
                meshRef = m;
                break;
            }
        }

        if (meshRef != null && meshRef.Vertices != null)
        {
            var posList = new List<float>();
            var colList = new List<float>();
            foreach (var v in meshRef.Vertices)
            {
                posList.AddRange(v.Pos);
                colList.AddRange(v.Color);
            }
            Debug($"[Renderer] Seeding MeshComponent on entity {ent}: {meshRef.Vertices.Count} vertices", "Renderer");
            _world.Set(ent, MeshComponent.Create(posList.ToArray(), colList.ToArray()));
        }

        Info($"[Renderer] Compiling render graph with {scene.Passes.Count} pass(es)...", "Renderer");
        var passes = new List<RenderPass>();
        foreach (var scenePass in scene.Passes)
            passes.Add(new HelloTrianglePass(_device, _world, scene, scenePass, contentRoot));

        // Capture the previous graph and only dispose it AFTER the new graph
        // is built. If Compile throws, the previous (working) graph remains
        // intact and usable. This avoids a temporary null-state failure
        // where neither old nor new graph is reachable.
        var previous = _plan;
        var newPlan = new RenderGraphCompiler().Compile(passes);
        _plan = newPlan;
        previous?.Passes?.DisposeAll();
        Info("[Renderer] Render graph compiled successfully", "Renderer");
    }

    public void RenderFrame(RhiTexture backBuffer, uint width, uint height)
    {
        if (_plan is null) return;
        using var executor = new RenderGraphExecutor(_device);
        executor.SetViewportSize(width, height);
        executor.BindSwapchain(backBuffer, BackBufferHandle,
                                ResourceState.RenderTarget);
        executor.Execute(_plan);
    }

    public void Dispose()
    {
        _plan?.Passes?.DisposeAll();
        _plan = null;
        _loader = null;
    }
}

internal static class RenderPassEnumerableExtensions
{
    public static void DisposeAll(this IEnumerable<RenderPass> passes)
    {
        foreach (var p in passes)
            if (p is IDisposable d) d.Dispose();
    }
}
