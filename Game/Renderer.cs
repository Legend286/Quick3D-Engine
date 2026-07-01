// SPDX-License-Identifier: MIT
// Renderer orchestration: takes a Scene, builds the pass list, compiles the
// render graph, drives the executor each frame.
//
// World ownership: the renderer does NOT own the EcsWorld; the caller hands
// it in at construction. This keeps the renderer free of GC pressure and
// lets the editor share a single world across multiple viewports later.

using System;
using System.Collections.Generic;
using System.Numerics;
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
    private readonly ImGuiRenderer? _imguiRenderer;

    private RenderPlan? _plan;

    /// <summary>Sentinel handle on which the executor binds the swapchain
    /// back-buffer before each frame. Console-friendly constant so callers
    /// can reference the same handle.</summary>
    public static readonly ResourceHandle BackBufferHandle = new(0x80000000);

    public Renderer(RhiDevice device, RhiSwapchain swap, IEntityStore world, ImGuiRenderer? imguiRenderer = null)
    {
        _device = device;
        _swap = swap;
        _world = world;
        _imguiRenderer = imguiRenderer;
    }

    public IEntityStore World => _world;

    public void LoadScene(string contentRoot, string sceneName)
    {
        _contentRoot = contentRoot;
        _loader = new SceneLoader(contentRoot);
        SceneGraph scene = _loader.Load(sceneName);

        _world.Clear();

        foreach (var modelRef in scene.Models)
        {
            var mdlPath = Path.Combine(_contentRoot, modelRef.Source);
            var model = Engine.Assets.ModelLoader.LoadMdl(_device, mdlPath);
            
            // Register all meshes and materials in the model parts
            for (int i = 0; i < model.Parts.Length; i++)
            {
                if (model.Parts[i].Mesh != null)
                {
                    ulong meshId = Engine.Assets.AssetRegistry.RegisterMesh(model.Parts[i].Mesh);
                }
            }

            ulong modelId = Engine.Assets.AssetRegistry.RegisterModel(model);

            ulong ent = _world.CreateEntity();
            _world.Set(ent, ModelComponent.Create(modelId));
            
            _world.Set(ent, new Engine.Scene.Components.Transform {
                Position = new Vector3(modelRef.Position[0], modelRef.Position[1], modelRef.Position[2]),
                Rotation = Quaternion.CreateFromYawPitchRoll(modelRef.Rotation[1], modelRef.Rotation[0], modelRef.Rotation[2]),
                Scale = new Vector3(modelRef.Scale[0], modelRef.Scale[1], modelRef.Scale[2])
            });
        }

        var passes = new List<RenderPass>();
        foreach (var scenePass in scene.Passes)
            passes.Add(new PbrPass(_device, _world, scene, scenePass, contentRoot));
            
        passes.Add(new GridPass(_device, _world, contentRoot, clearScreen: scene.Passes.Count == 0));
            
        if (_imguiRenderer != null)
            passes.Add(new ImGuiPass(_imguiRenderer));

        var previous = _plan;
        
        Info($"[Renderer] Compiling render graph with {passes.Count} pass(es)...", "Renderer");
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
