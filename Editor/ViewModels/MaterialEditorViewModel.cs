// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Engine.Editor.Views;
using Engine.RHI;

namespace Engine.Editor.ViewModels;

public partial class MaterialEditorViewModel : ObservableObject, IDisposable
{
    private readonly string _materialPath;
    private DispatcherTimer _timer;
    private RhiDevice? _device;
    private RhiSwapchain? _swap;
    private IGameLoop? _gameLoop;
    private GameAssemblyLoadContext? _loadContext;
    private EcsWorld? _world;
    private ViewportMetalLayerHost? _host;
    private uint _width = 1280;
    private uint _height = 720;
    private bool _isLoading;
    
    // Camera controls
    private float _camYaw = 0f;
    private float _camPitch = 0f;
    private float _camDist = 5f;
    private ulong _cameraEntity;

    [ObservableProperty] private float _baseColorR;
    [ObservableProperty] private float _baseColorG;
    [ObservableProperty] private float _baseColorB;
    [ObservableProperty] private float _metallic;
    [ObservableProperty] private float _roughness;
    [ObservableProperty] private float _subsurface;
    [ObservableProperty] private float _subsurfaceColorR;
    [ObservableProperty] private float _subsurfaceColorG;
    [ObservableProperty] private float _subsurfaceColorB;
    [ObservableProperty] private float _subsurfaceRadiusR;
    [ObservableProperty] private float _subsurfaceRadiusG;
    [ObservableProperty] private float _subsurfaceRadiusB;
    [ObservableProperty] private float _clearcoat;
    [ObservableProperty] private float _clearcoatRoughness;
    [ObservableProperty] private float _topColorR;
    [ObservableProperty] private float _topColorG;
    [ObservableProperty] private float _topColorB;
    [ObservableProperty] private float _topMetallic;
    [ObservableProperty] private float _topRoughness;
    [ObservableProperty] private int _topMaskType;

    public MaterialEditorViewModel(string materialPath)
    {
        _materialPath = materialPath;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        
        LoadMaterialData();
    }

    private void LoadMaterialData()
    {
        if (!File.Exists(_materialPath)) return;
        try {
            _isLoading = true;
            var json = File.ReadAllText(_materialPath);
            var doc = JsonNode.Parse(json);
            if (doc != null) {
                if (doc["albedo_color"] is JsonArray albedo) {
                    BaseColorR = (float)albedo[0]!;
                    BaseColorG = (float)albedo[1]!;
                    BaseColorB = (float)albedo[2]!;
                }
                if (doc["metallic"] != null) Metallic = (float)doc["metallic"]!;
                if (doc["roughness"] != null) Roughness = (float)doc["roughness"]!;
                if (doc["subsurface"] != null) Subsurface = (float)doc["subsurface"]!;
                if (doc["subsurface_color"] is JsonArray sssColor) {
                    SubsurfaceColorR = (float)sssColor[0]!;
                    SubsurfaceColorG = (float)sssColor[1]!;
                    SubsurfaceColorB = (float)sssColor[2]!;
                }
                if (doc["subsurface_radius"] is JsonArray sssRadius) {
                    SubsurfaceRadiusR = (float)sssRadius[0]!;
                    SubsurfaceRadiusG = (float)sssRadius[1]!;
                    SubsurfaceRadiusB = (float)sssRadius[2]!;
                }
                if (doc["clearcoat"] != null) Clearcoat = (float)doc["clearcoat"]!;
                if (doc["clearcoat_roughness"] != null) ClearcoatRoughness = (float)doc["clearcoat_roughness"]!;
                
                if (doc["top_color"] is JsonArray topAlbedo) {
                    TopColorR = (float)topAlbedo[0]!;
                    TopColorG = (float)topAlbedo[1]!;
                    TopColorB = (float)topAlbedo[2]!;
                }
                if (doc["top_metallic"] != null) TopMetallic = (float)doc["top_metallic"]!;
                if (doc["top_roughness"] != null) TopRoughness = (float)doc["top_roughness"]!;
                if (doc["top_mask_type"] != null) TopMaskType = (int)doc["top_mask_type"]!;
            }
        } 
        catch {}
        finally {
            _isLoading = false;
        }
    }

    private void SaveMaterialData()
    {
        if (!File.Exists(_materialPath)) return;
        try {
            var json = File.ReadAllText(_materialPath);
            var doc = JsonNode.Parse(json) as JsonObject;
            if (doc != null) {
                doc["albedo_color"] = new JsonArray(BaseColorR, BaseColorG, BaseColorB, 1.0f);
                doc["metallic"] = Metallic;
                doc["roughness"] = Roughness;
                doc["subsurface"] = Subsurface;
                doc["subsurface_color"] = new JsonArray(SubsurfaceColorR, SubsurfaceColorG, SubsurfaceColorB);
                doc["subsurface_radius"] = new JsonArray(SubsurfaceRadiusR, SubsurfaceRadiusG, SubsurfaceRadiusB);
                doc["clearcoat"] = Clearcoat;
                doc["clearcoat_roughness"] = ClearcoatRoughness;
                
                doc["top_color"] = new JsonArray(TopColorR, TopColorG, TopColorB, 1.0f);
                doc["top_metallic"] = TopMetallic;
                doc["top_roughness"] = TopRoughness;
                doc["top_mask_type"] = TopMaskType;
                
                // Atomic save
                string tmp = _materialPath + ".tmp";
                File.WriteAllText(tmp, doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, _materialPath, true);
            }
        } catch {}
    }

    partial void OnBaseColorRChanged(float value) => PushMaterialToPreview();
    partial void OnBaseColorGChanged(float value) => PushMaterialToPreview();
    partial void OnBaseColorBChanged(float value) => PushMaterialToPreview();
    partial void OnMetallicChanged(float value) => PushMaterialToPreview();
    partial void OnRoughnessChanged(float value) => PushMaterialToPreview();
    partial void OnSubsurfaceChanged(float value) => PushMaterialToPreview();
    partial void OnSubsurfaceColorRChanged(float value) => PushMaterialToPreview();
    partial void OnSubsurfaceColorGChanged(float value) => PushMaterialToPreview();
    partial void OnSubsurfaceColorBChanged(float value) => PushMaterialToPreview();
    partial void OnSubsurfaceRadiusRChanged(float value) => PushMaterialToPreview();
    partial void OnSubsurfaceRadiusGChanged(float value) => PushMaterialToPreview();
    partial void OnSubsurfaceRadiusBChanged(float value) => PushMaterialToPreview();
    partial void OnClearcoatChanged(float value) => PushMaterialToPreview();
    partial void OnClearcoatRoughnessChanged(float value) => PushMaterialToPreview();
    partial void OnTopColorRChanged(float value) => PushMaterialToPreview();
    partial void OnTopColorGChanged(float value) => PushMaterialToPreview();
    partial void OnTopColorBChanged(float value) => PushMaterialToPreview();
    partial void OnTopMetallicChanged(float value) => PushMaterialToPreview();
    partial void OnTopRoughnessChanged(float value) => PushMaterialToPreview();
    partial void OnTopMaskTypeChanged(int value) => PushMaterialToPreview();

    private void PushMaterialToPreview()
    {
        if (_isLoading) return;
        if (_gameLoop != null)
        {
            _gameLoop.UpdateMaterialPreview(
                new[] { BaseColorR, BaseColorG, BaseColorB, 1.0f },
                Metallic,
                Roughness,
                Subsurface,
                new[] { SubsurfaceColorR, SubsurfaceColorG, SubsurfaceColorB },
                new[] { SubsurfaceRadiusR, SubsurfaceRadiusG, SubsurfaceRadiusB },
                Clearcoat,
                ClearcoatRoughness,
                new[] { TopColorR, TopColorG, TopColorB, 1.0f },
                TopMetallic,
                TopRoughness,
                (uint)TopMaskType
            );
        }
        SaveMaterialData();
    }

    public void AddPointerDelta(float dx, float dy)
    {
        _camYaw -= dx * 0.01f;
        _camPitch += dy * 0.01f;
        _camPitch = Math.Clamp(_camPitch, -1.5f, 1.5f);
    }

    public void AddScrollDelta(float dy)
    {
        _camDist -= dy * 0.5f;
        _camDist = Math.Clamp(_camDist, 1.0f, 20.0f);
    }

    public void AttachToVisualTree(Window win, ViewportMetalLayerHost host)
    {
        _host = host;
        if (_host.NativeViewHandle == IntPtr.Zero) return;

        double scale = win.RenderScaling;
        _width = (uint)Math.Max(1, _host.Bounds.Width * scale);
        _height = (uint)Math.Max(1, _host.Bounds.Height * scale);

        _device = new RhiDevice();
        
        _swap = _device.CreateSwapchain(_host.NativeViewHandle, _width, _height);
        _world = new EcsWorld();

        LoadGameLoop();
    }

    private void LoadGameLoop()
    {
        string dllPath = Path.Combine(App.ProjectRoot, "Game", "bin", "Release", "net8.0", "osx-arm64", "Engine.Game.dll");
        if (!File.Exists(dllPath))
            dllPath = Path.Combine(App.ProjectRoot, "Game", "bin", "Release", "net8.0", "Engine.Game.dll");

        _loadContext = new GameAssemblyLoadContext(dllPath);
        var assembly = _loadContext.LoadFromAssemblyName(new System.Reflection.AssemblyName("Engine.Game"));
        
        Type? loopType = null;
        foreach (var type in assembly.GetTypes())
        {
            if (typeof(IGameLoop).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
            {
                loopType = type;
                break;
            }
        }

        _gameLoop = (IGameLoop)Activator.CreateInstance(loopType!, false)!;
        _gameLoop.Init(_device!.Handle, _swap!.Handle, _world!, false);

        CreatePreviewScene();
        
        _timer.Start();
    }

    private void CreatePreviewScene()
    {
        string contentRoot = Path.Combine(App.ProjectRoot, "Content");
        _gameLoop!.LoadMaterialPreview(contentRoot, _materialPath);

        // Find camera entity to move it
        foreach (var ent in _world!.Entities)
        {
            if (_world.TryGet<Engine.Scene.Components.Camera>(ent, out _))
            {
                _cameraEntity = ent;
                break;
            }
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_device is null || _swap is null || _gameLoop is null) return;
        
        // Update camera position based on orbit
        if (_cameraEntity != 0 && _world != null)
        {
            if (_world.TryGet<Engine.Scene.Components.Transform>(_cameraEntity, out var t))
            {
                var rot = Quaternion.CreateFromYawPitchRoll(_camYaw, _camPitch, 0);
                var pos = Vector3.Transform(new Vector3(0, 0, _camDist), rot);
                t.Position = pos;
                
                // Point camera at origin
                var forward = Vector3.Normalize(-pos);
                // Simple look-at rotation (assuming up is Y)
                t.Rotation = rot; // Since we orbit from origin, the orbit rotation is exactly the camera rotation to look at origin
                _world.Set(_cameraEntity, t);
            }
        }

        if (!_swap.TryAcquireNextImage(out RhiTexture? image) || image is null) return;
        
        try
        {
            var input = new Engine.RHI.InputState { DeltaTime = 0.016f };
            _gameLoop.Update(input);
            _gameLoop.RenderFrame(image, _swap.Width, _swap.Height);
        }
        catch {}
        finally
        {
            _swap.Present();
            image.Dispose();
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _gameLoop?.Dispose();
        _loadContext?.Unload();
        _world?.Dispose();
    }
}
