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

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;

namespace Engine.Editor.ViewModels;

public partial class MaterialLayerViewModel : ObservableObject
{
    private readonly Action _onChanged;

    [ObservableProperty] private string _name;
    [ObservableProperty] private float _colorR = 1.0f;
    [ObservableProperty] private float _colorG = 1.0f;
    [ObservableProperty] private float _colorB = 1.0f;
    [ObservableProperty] private float _metallic = 0.0f;
    [ObservableProperty] private float _roughness = 1.0f;
    [ObservableProperty] private int _maskType = 0; // 0=None, 1=3D Perlin, 2=Voronoi, 3=FBM, 4=Turbulence, 5=Cavity, 6=Height, 7=Texture Mask
    [ObservableProperty] private float _noiseScale = 10.0f;
    [ObservableProperty] private int _noiseDetail = 3;
    [ObservableProperty] private float _noiseThreshold = 0.5f;
    [ObservableProperty] private string _albedoTexture = "";
    [ObservableProperty] private string _normalTexture = "";
    [ObservableProperty] private string _rmaTexture = "";
    [ObservableProperty] private string _maskTexture = "";

    public MaterialLayerViewModel(string name, Action onChanged)
    {
        _name = name;
        _onChanged = onChanged;
    }

    partial void OnColorRChanged(float value) => _onChanged();
    partial void OnColorGChanged(float value) => _onChanged();
    partial void OnColorBChanged(float value) => _onChanged();
    partial void OnMetallicChanged(float value) => _onChanged();
    partial void OnRoughnessChanged(float value) => _onChanged();
    partial void OnMaskTypeChanged(int value) => _onChanged();
    partial void OnNoiseScaleChanged(float value) => _onChanged();
    partial void OnNoiseDetailChanged(int value) => _onChanged();
    partial void OnNoiseThresholdChanged(float value) => _onChanged();
    partial void OnAlbedoTextureChanged(string value) => _onChanged();
    partial void OnNormalTextureChanged(string value) => _onChanged();
    partial void OnRmaTextureChanged(string value) => _onChanged();
    partial void OnMaskTextureChanged(string value) => _onChanged();
}


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

    [ObservableProperty] private float _baseColorR = 1.0f;
    [ObservableProperty] private float _baseColorG = 1.0f;
    [ObservableProperty] private float _baseColorB = 1.0f;
    [ObservableProperty] private float _metallic = 0.0f;
    [ObservableProperty] private float _roughness = 1.0f;
    [ObservableProperty] private string _baseAlbedoTexture = "";
    [ObservableProperty] private string _baseNormalTexture = "";
    [ObservableProperty] private string _baseRmaTexture = "";

    [ObservableProperty] private float _subsurface;
    [ObservableProperty] private float _subsurfaceColorR = 1.0f;
    [ObservableProperty] private float _subsurfaceColorG = 1.0f;
    [ObservableProperty] private float _subsurfaceColorB = 1.0f;
    [ObservableProperty] private float _subsurfaceRadiusR = 1.0f;
    [ObservableProperty] private float _subsurfaceRadiusG = 0.2f;
    [ObservableProperty] private float _subsurfaceRadiusB = 0.1f;
    [ObservableProperty] private float _clearcoat;
    [ObservableProperty] private float _clearcoatRoughness;

    [ObservableProperty] private ObservableCollection<MaterialLayerViewModel> _layers = new();

    public MaterialEditorViewModel(string materialPath)
    {
        _materialPath = materialPath;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        
        LoadMaterialData();
    }

    [RelayCommand]
    public void AddLayer()
    {
        var layer = new MaterialLayerViewModel($"Layer {Layers.Count + 1}", PushMaterialToPreview);
        Layers.Add(layer);
        PushMaterialToPreview();
    }

    [RelayCommand]
    public void RemoveLayer(MaterialLayerViewModel? layer)
    {
        if (layer != null && Layers.Contains(layer))
        {
            Layers.Remove(layer);
            PushMaterialToPreview();
        }
    }

    [RelayCommand]
    public void MoveLayerUp(MaterialLayerViewModel? layer)
    {
        if (layer == null) return;
        int idx = Layers.IndexOf(layer);
        if (idx > 0)
        {
            Layers.Move(idx, idx - 1);
            PushMaterialToPreview();
        }
    }

    [RelayCommand]
    public void MoveLayerDown(MaterialLayerViewModel? layer)
    {
        if (layer == null) return;
        int idx = Layers.IndexOf(layer);
        if (idx >= 0 && idx < Layers.Count - 1)
        {
            Layers.Move(idx, idx + 1);
            PushMaterialToPreview();
        }
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
                if (doc["albedo_texture"] != null) BaseAlbedoTexture = (string)doc["albedo_texture"]!;
                if (doc["normal_texture"] != null) BaseNormalTexture = (string)doc["normal_texture"]!;
                if (doc["rma_texture"] != null) BaseRmaTexture = (string)doc["rma_texture"]!;

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
                
                Layers.Clear();
                if (doc["layers"] is JsonArray layersArray)
                {
                    foreach (var node in layersArray)
                    {
                        if (node is JsonObject lobj)
                        {
                            string layerName = lobj["name"]?.ToString() ?? "Layer";
                            var lvm = new MaterialLayerViewModel(layerName, PushMaterialToPreview);


                            if (lobj["albedo_color"] is JsonArray col) {
                                lvm.ColorR = (float)col[0]!;
                                lvm.ColorG = (float)col[1]!;
                                lvm.ColorB = (float)col[2]!;
                            }
                            if (lobj["metallic"] != null) lvm.Metallic = (float)lobj["metallic"]!;
                            if (lobj["roughness"] != null) lvm.Roughness = (float)lobj["roughness"]!;
                            if (lobj["mask_type"] != null) lvm.MaskType = (int)lobj["mask_type"]!;
                            if (lobj["noise_scale"] != null) lvm.NoiseScale = (float)lobj["noise_scale"]!;
                            if (lobj["noise_detail"] != null) lvm.NoiseDetail = (int)lobj["noise_detail"]!;
                            if (lobj["noise_threshold"] != null) lvm.NoiseThreshold = (float)lobj["noise_threshold"]!;
                            if (lobj["albedo_texture"] != null) lvm.AlbedoTexture = (string)lobj["albedo_texture"]!;
                            if (lobj["normal_texture"] != null) lvm.NormalTexture = (string)lobj["normal_texture"]!;
                            if (lobj["rma_texture"] != null) lvm.RmaTexture = (string)lobj["rma_texture"]!;
                            if (lobj["mask_texture"] != null) lvm.MaskTexture = (string)lobj["mask_texture"]!;
                            Layers.Add(lvm);
                        }
                    }
                }
                else if (doc["top_color"] != null || doc["top_mask_type"] != null)
                {
                    // Convert legacy top layer into layer stack
                    var lvm = new MaterialLayerViewModel("Top Layer", PushMaterialToPreview);
                    if (doc["top_color"] is JsonArray topAlbedo) {
                        lvm.ColorR = (float)topAlbedo[0]!;
                        lvm.ColorG = (float)topAlbedo[1]!;
                        lvm.ColorB = (float)topAlbedo[2]!;
                    }
                    if (doc["top_metallic"] != null) lvm.Metallic = (float)doc["top_metallic"]!;
                    if (doc["top_roughness"] != null) lvm.Roughness = (float)doc["top_roughness"]!;
                    if (doc["top_mask_type"] != null) lvm.MaskType = (int)doc["top_mask_type"]!;
                    Layers.Add(lvm);
                }
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
                if (!string.IsNullOrEmpty(BaseAlbedoTexture)) doc["albedo_texture"] = BaseAlbedoTexture;
                if (!string.IsNullOrEmpty(BaseNormalTexture)) doc["normal_texture"] = BaseNormalTexture;
                if (!string.IsNullOrEmpty(BaseRmaTexture)) doc["rma_texture"] = BaseRmaTexture;

                doc["subsurface"] = Subsurface;
                doc["subsurface_color"] = new JsonArray(SubsurfaceColorR, SubsurfaceColorG, SubsurfaceColorB);
                doc["subsurface_radius"] = new JsonArray(SubsurfaceRadiusR, SubsurfaceRadiusG, SubsurfaceRadiusB);
                doc["clearcoat"] = Clearcoat;
                doc["clearcoat_roughness"] = ClearcoatRoughness;
                
                var larr = new JsonArray();
                foreach (var l in Layers)
                {
                    var lobj = new JsonObject();
                    lobj["name"] = l.Name;
                    lobj["albedo_color"] = new JsonArray(l.ColorR, l.ColorG, l.ColorB, 1.0f);
                    lobj["metallic"] = l.Metallic;
                    lobj["roughness"] = l.Roughness;
                    lobj["mask_type"] = l.MaskType;
                    lobj["noise_scale"] = l.NoiseScale;
                    lobj["noise_detail"] = l.NoiseDetail;
                    lobj["noise_threshold"] = l.NoiseThreshold;
                    if (!string.IsNullOrEmpty(l.AlbedoTexture)) lobj["albedo_texture"] = l.AlbedoTexture;
                    if (!string.IsNullOrEmpty(l.NormalTexture)) lobj["normal_texture"] = l.NormalTexture;
                    if (!string.IsNullOrEmpty(l.RmaTexture)) lobj["rma_texture"] = l.RmaTexture;
                    if (!string.IsNullOrEmpty(l.MaskTexture)) lobj["mask_texture"] = l.MaskTexture;
                    larr.Add(lobj);
                }

                doc["layers"] = larr;

                if (Layers.Count > 0)
                {
                    var top = Layers[0];
                    doc["top_color"] = new JsonArray(top.ColorR, top.ColorG, top.ColorB, 1.0f);
                    doc["top_metallic"] = top.Metallic;
                    doc["top_roughness"] = top.Roughness;
                    doc["top_mask_type"] = top.MaskType;
                }
                
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
    partial void OnBaseAlbedoTextureChanged(string value) => PushMaterialToPreview();
    partial void OnBaseNormalTextureChanged(string value) => PushMaterialToPreview();
    partial void OnBaseRmaTextureChanged(string value) => PushMaterialToPreview();

    partial void OnSubsurfaceChanged(float value) => PushMaterialToPreview();
    partial void OnSubsurfaceColorRChanged(float value) => PushMaterialToPreview();
    partial void OnSubsurfaceColorGChanged(float value) => PushMaterialToPreview();
    partial void OnSubsurfaceColorBChanged(float value) => PushMaterialToPreview();
    partial void OnSubsurfaceRadiusRChanged(float value) => PushMaterialToPreview();
    partial void OnSubsurfaceRadiusGChanged(float value) => PushMaterialToPreview();
    partial void OnSubsurfaceRadiusBChanged(float value) => PushMaterialToPreview();
    partial void OnClearcoatChanged(float value) => PushMaterialToPreview();
    partial void OnClearcoatRoughnessChanged(float value) => PushMaterialToPreview();

    private void PushMaterialToPreview()
    {
        if (_isLoading) return;
        if (_gameLoop != null)
        {
            float[] topCol = Layers.Count > 0 ? new[] { Layers[0].ColorR, Layers[0].ColorG, Layers[0].ColorB, 1.0f } : new[] { 1.0f, 1.0f, 1.0f, 1.0f };
            float topMet = Layers.Count > 0 ? Layers[0].Metallic : 0.0f;
            float topRough = Layers.Count > 0 ? Layers[0].Roughness : 1.0f;
            uint topMask = Layers.Count > 0 ? (uint)Layers[0].MaskType : 0u;

            _gameLoop.UpdateMaterialPreview(
                new[] { BaseColorR, BaseColorG, BaseColorB, 1.0f },
                Metallic,
                Roughness,
                Subsurface,
                new[] { SubsurfaceColorR, SubsurfaceColorG, SubsurfaceColorB },
                new[] { SubsurfaceRadiusR, SubsurfaceRadiusG, SubsurfaceRadiusB },
                Clearcoat,
                ClearcoatRoughness,
                topCol,
                topMet,
                topRough,
                topMask
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
