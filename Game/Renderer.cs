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
using System.IO;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.Scene;
using Engine.Assets;
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
    private SceneGraph? _currentScene;

    /// <summary>Sentinel handle on which the executor binds the swapchain
    /// back-buffer before each frame. Console-friendly constant so callers
    /// can reference the same handle.</summary>
    public static readonly ResourceHandle BackBufferHandle = new(0x80000000);
    public static readonly ResourceHandle DepthBufferHandle = new(0x80000001);

    private string _lastSceneName = "";
    private bool _usePathTracer = true;
    private RhiBindlessHeap _sharedBindlessHeap;

    private RhiTexture? _depthTexture;
    private uint _depthWidth, _depthHeight;

    public bool UsePathTracer
    {
        get => _usePathTracer;
        set
        {
            if (_usePathTracer != value)
            {
                _usePathTracer = value;
                if (_currentScene != null)
                    RebuildRenderPlan(_currentScene, _contentRoot);
            }
        }
    }

    public Renderer(RhiDevice device, RhiSwapchain swap, IEntityStore world, ImGuiRenderer? imguiRenderer = null)
    {
        _device = device;
        _swap = swap;
        _world = world;
        _imguiRenderer = imguiRenderer;
        _sharedBindlessHeap = new RhiBindlessHeap(_device, 4096);
    }

    public IEntityStore World => _world;

    public void LoadScene(string contentRoot, string sceneName)
    {
        _contentRoot = contentRoot;
        _lastSceneName = sceneName;
        _loader = new SceneLoader(contentRoot);
        SceneGraph scene = _loader.Load(sceneName);

        _world.Clear();

        foreach (var modelRef in scene.Models)
        {
            var mdlPath = Path.Combine(_contentRoot, modelRef.Source);
            if (!File.Exists(mdlPath))
                mdlPath = Path.Combine(_contentRoot, "assets", Path.GetFileName(modelRef.Source));
                
            Model? model = null;
            try 
            {
                model = Engine.Assets.ModelLoader.LoadMdl(_device, mdlPath);
            }
            catch (Exception ex)
            {
                Error($"[Renderer] Failed to load model '{mdlPath}': {ex.Message}", "Renderer");
                continue;
            }
            
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
            
            var pos = modelRef.Position ?? new float[] { 0, 0, 0 };
            var rot = modelRef.Rotation ?? new float[] { 0, 0, 0, 1 };
            var scl = modelRef.Scale ?? new float[] { 1, 1, 1 };
            
            Quaternion q = Quaternion.Identity;
            if (rot.Length >= 4)
                q = new Quaternion(rot[0], rot[1], rot[2], rot[3]);
            else if (rot.Length == 3)
                q = Quaternion.CreateFromYawPitchRoll(rot[1] * MathF.PI / 180f, rot[0] * MathF.PI / 180f, rot[2] * MathF.PI / 180f);

            _world.Set(ent, new Engine.Scene.Components.Transform {
                Position = pos.Length >= 3 ? new Vector3(pos[0], pos[1], pos[2]) : Vector3.Zero,
                Rotation = q,
                Scale = scl.Length >= 3 ? new Vector3(scl[0], scl[1], scl[2]) : Vector3.One
            });
        }

        _currentScene = scene;
        RebuildRenderPlan(scene, contentRoot);
    }

    private void RebuildRenderPlan(SceneGraph scene, string contentRoot)
    {
        var passes = new List<RenderPass>();
        foreach (var scenePass in scene.Passes)
        {
            if (_usePathTracer)
                passes.Add(new PathTracerPass(_device, _world, scene, scenePass, contentRoot, _sharedBindlessHeap));
            else
                passes.Add(new PbrPass(_device, _world, scene, scenePass, contentRoot, _sharedBindlessHeap));
        }
            
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
        
        if (_depthTexture == null || _depthWidth != width || _depthHeight != height)
        {
            _depthTexture?.Dispose();
            _depthWidth = width > 0 ? width : 1;
            _depthHeight = height > 0 ? height : 1;
            
            var desc = new Engine.CBindings.RhiNative.TextureDesc
            {
                Abi = 1,
                Width = _depthWidth,
                Height = _depthHeight,
                MipLevels = 1,
                Format = Engine.CBindings.RhiNative.TextureFormat.Depth32Float,
                UsageFlags = Engine.CBindings.RhiNative.TextureRenderTarget
            };
            _depthTexture = RhiTexture.CreateDepth(_device, _depthWidth, _depthHeight);
        }

        using var executor = new RenderGraphExecutor(_device);
        executor.SetViewportSize(width, height);
        executor.BindSwapchain(backBuffer, BackBufferHandle, ResourceState.RenderTarget);
        if (_depthTexture != null)
            executor.BindSwapchain(_depthTexture, DepthBufferHandle, ResourceState.DepthStencil);
            
        executor.Execute(_plan);
    }

    public void Dispose()
    {
        _plan?.Passes?.DisposeAll();
        _plan = null;
        _loader = null;
        _depthTexture?.Dispose();
        _depthTexture = null;
        _sharedBindlessHeap?.Dispose();
        _sharedBindlessHeap = null!;
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
