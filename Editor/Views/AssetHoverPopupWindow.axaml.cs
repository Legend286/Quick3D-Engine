// SPDX-License-Identifier: MIT
using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Engine.CBindings;
using Engine.Editor.ViewModels;
using Engine.RHI;

namespace Engine.Editor.Views;

public partial class AssetHoverPopupWindow : Window
{
    private const uint LivePreviewSize = 256;
    private readonly DispatcherTimer _livePreviewTimer;
    private ViewportPanelViewModel? _viewport;
    private IGameLoop? _previewLoop;
    private EcsWorld? _previewWorld;
    private string? _liveAssetPath;
    private string? _liveAssetType;
    private RhiTexture? _liveTexture;
    private RhiFence? _liveFence;
    private IntPtr _externalImageHandle;
    private IntPtr _externalSemaphoreHandle;
    private float _orbitRadians;
    private ulong _nextWaitValue;
    private ulong _nextSignalValue;
    private bool _refreshInFlight;

    public AssetHoverPopupWindow()
    {
        InitializeComponent();
        _livePreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _livePreviewTimer.Tick += OnLivePreviewTick;
        Closed += OnClosed;
    }

    public void UpdatePreview(Bitmap? bitmap, string title, string assetType)
    {
        StopLivePreview();
        var image = this.FindControl<Image>("PreviewImage")!;
        var gpuPreview = this.FindControl<GpuExternalImageControl>("GpuPreviewControl")!;
        image.Source = bitmap;
        image.IsVisible = true;
        gpuPreview.IsVisible = true;
        gpuPreview.Opacity = 0;
        this.FindControl<TextBlock>("TitleText")!.Text = title;
        this.FindControl<TextBlock>("TypeText")!.Text = assetType;
    }

    public async void StartLivePreview(ViewportPanelViewModel viewport, string assetPath, string assetType, string title, Bitmap? fallbackBitmap = null)
    {
        if (assetType != "Model" && assetType != "Material")
        {
            StopLivePreview();
            return;
        }

        var image = this.FindControl<Image>("PreviewImage")!;
        var gpuPreview = this.FindControl<GpuExternalImageControl>("GpuPreviewControl")!;

        try
        {
            bool viewportChanged = _viewport != viewport;
            bool assetChanged = _liveAssetPath != assetPath || _liveAssetType != assetType || viewportChanged;
            _viewport = viewport;
            _liveAssetPath = assetPath;
            _liveAssetType = assetType;
            _orbitRadians = 0.0f;

            this.FindControl<TextBlock>("TitleText")!.Text = title;
            this.FindControl<TextBlock>("TypeText")!.Text = assetType;
            if (fallbackBitmap != null)
                image.Source = fallbackBitmap;
            image.IsVisible = image.Source != null;
            if (assetChanged)
                gpuPreview.Opacity = 0;

            if (assetChanged)
            {
                if (viewportChanged)
                    DisposeLiveTexture();
                DisposePreviewLoop();
                if (viewport.Device == null || viewport.Swapchain == null || viewport.GameLoop == null)
                    return;

                _previewWorld = new EcsWorld();
                _previewLoop = CreatePreviewLoop(viewport, _previewWorld);
                if (_liveTexture == null || _liveFence == null)
                {
                    _liveTexture = RhiTexture.CreateExternalRenderTarget(viewport.Device, LivePreviewSize, LivePreviewSize, RhiNative.TextureFormat.Bgra8Unorm);
                    _liveFence = new RhiFence(viewport.Device);
                    var exported = _liveTexture.ExportExternalImage();
                    var exportedSemaphore = _liveFence.ExportExternalHandle();
                    _externalImageHandle = exported.Handle;
                    _externalSemaphoreHandle = exportedSemaphore.Handle;
                    _nextWaitValue = 0;
                    _nextSignalValue = 1;
                    await gpuPreview.SetExternalImageAsync(exported.Handle, exported.Width, exported.Height, MapExternalImageFormat(exported.Format), exportedSemaphore.Handle);
                }
                if (assetType == "Model")
                    _previewLoop.LoadModelPreview(viewport.ContentRoot, assetPath);
                else
                    _previewLoop.LoadMaterialPreview(viewport.ContentRoot, assetPath, usePathTracer: false);
            }

            if (!_refreshInFlight && RenderLivePreviewFrame())
            {
                _refreshInFlight = true;
                bool refreshed = await gpuPreview.RefreshAsync(_nextSignalValue, _nextSignalValue + 1);
                _refreshInFlight = false;
                if (refreshed)
                {
                    _nextWaitValue = _nextSignalValue + 1;
                    _nextSignalValue = _nextWaitValue + 1;
                    gpuPreview.Opacity = 1;
                    image.IsVisible = false;
                    _livePreviewTimer.Start();
                }
                else
                {
                    _livePreviewTimer.Start();
                }
            }
        }
        catch (Exception ex)
        {
            Engine.CBindings.Log.Error($"[HoverPreview] Live preview failed: {ex}", "Editor");
            gpuPreview.Opacity = 0;
            image.IsVisible = image.Source != null;
            _livePreviewTimer.Stop();
            _refreshInFlight = false;
            DisposeLiveTexture();
            DisposePreviewLoop();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        StopLivePreview();
        DisposeLiveTexture();
        _viewport = null;
    }

    private async void OnLivePreviewTick(object? sender, EventArgs e)
    {
        try
        {
            if (_refreshInFlight)
                return;
            if (!RenderLivePreviewFrame())
                return;
            var gpuPreview = this.FindControl<GpuExternalImageControl>("GpuPreviewControl")!;
            _refreshInFlight = true;
            bool refreshed = await gpuPreview.RefreshAsync(_nextSignalValue, _nextSignalValue + 1);
            _refreshInFlight = false;
            if (!refreshed)
                return;
            _nextWaitValue = _nextSignalValue + 1;
            _nextSignalValue = _nextWaitValue + 1;
            _orbitRadians += 0.03f;
            gpuPreview.Opacity = 1;
            this.FindControl<Image>("PreviewImage")!.IsVisible = false;
        }
        catch (Exception ex)
        {
            Engine.CBindings.Log.Error($"[HoverPreview] Live preview tick failed: {ex}", "Editor");
            _livePreviewTimer.Stop();
            _refreshInFlight = false;
            this.FindControl<GpuExternalImageControl>("GpuPreviewControl")!.Opacity = 0;
            this.FindControl<Image>("PreviewImage")!.IsVisible = this.FindControl<Image>("PreviewImage")!.Source != null;
            DisposeLiveTexture();
            DisposePreviewLoop();
        }
    }

    private bool RenderLivePreviewFrame()
    {
        if (_previewLoop == null || _liveTexture == null || string.IsNullOrWhiteSpace(_liveAssetPath) || string.IsNullOrWhiteSpace(_liveAssetType))
            return false;

        _previewLoop.RenderLoadedPreview(_liveTexture, LivePreviewSize, LivePreviewSize, _orbitRadians, _liveFence, _nextWaitValue, _nextSignalValue);
        return true;
    }

    public void StopLivePreview()
    {
        _livePreviewTimer.Stop();
        _refreshInFlight = false;
        _liveAssetPath = null;
        _liveAssetType = null;
        _orbitRadians = 0.0f;
        DisposePreviewLoop();
    }

    private void DisposeLiveTexture()
    {
        this.FindControl<GpuExternalImageControl>("GpuPreviewControl")?.ResetExternalImage();
        _liveTexture?.Dispose();
        _liveTexture = null;
        _liveFence?.Dispose();
        _liveFence = null;
        if (_externalImageHandle != IntPtr.Zero)
        {
            RhiTexture.ReleaseExternalImageHandle(_externalImageHandle);
            _externalImageHandle = IntPtr.Zero;
        }
        if (_externalSemaphoreHandle != IntPtr.Zero)
        {
            RhiFence.ReleaseExternalSemaphoreHandle(_externalSemaphoreHandle);
            _externalSemaphoreHandle = IntPtr.Zero;
        }
    }

    private void DisposePreviewLoop()
    {
        _previewLoop?.Dispose();
        _previewLoop = null;
        _previewWorld?.Dispose();
        _previewWorld = null;
    }

    private static IGameLoop CreatePreviewLoop(ViewportPanelViewModel viewport, EcsWorld world)
    {
        if (viewport.GameLoop == null || viewport.Device == null || viewport.Swapchain == null)
            throw new InvalidOperationException("Viewport preview device is unavailable.");

        if (Activator.CreateInstance(viewport.GameLoop.GetType()) is not IGameLoop previewLoop)
            throw new InvalidOperationException("Failed to create preview game loop instance.");

        previewLoop.Init(viewport.Device.Handle, viewport.Swapchain.Handle, world, false);
        return previewLoop;
    }

    private static PlatformGraphicsExternalImageFormat MapExternalImageFormat(RhiNative.TextureFormat format)
    {
        return format switch
        {
            RhiNative.TextureFormat.Bgra8Unorm => PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
            RhiNative.TextureFormat.Rgba8Unorm => PlatformGraphicsExternalImageFormat.R8G8B8A8UNorm,
            _ => PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
        };
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
