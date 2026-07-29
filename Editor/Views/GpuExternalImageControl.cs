// SPDX-License-Identifier: MIT
using System;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Engine.CBindings;

namespace Engine.Editor.Views;

public sealed class GpuExternalImageControl : Control
{
    private CompositionDrawingSurface? _drawingSurface;
    private CompositionSurfaceVisual? _surfaceVisual;
    private ICompositionGpuInterop? _gpuInterop;
    private ICompositionImportedGpuImage? _importedImage;
    private ICompositionImportedGpuSemaphore? _importedSemaphore;
    private IntPtr _externalHandle;
    private IntPtr _externalSemaphoreHandle;
    private PixelSize _pixelSize;
    private PlatformGraphicsExternalImageFormat _imageFormat = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm;
    private bool _compositionReady;
    private bool _isAttached;
    private CompositionGpuImportedImageSynchronizationCapabilities _syncCapabilities;

    public async Task SetExternalImageAsync(IntPtr handle, uint width, uint height, PlatformGraphicsExternalImageFormat imageFormat, IntPtr semaphoreHandle)
    {
        bool handleChanged = _externalHandle != handle || _externalSemaphoreHandle != semaphoreHandle || _pixelSize.Width != (int)width || _pixelSize.Height != (int)height || _imageFormat != imageFormat;
        if (handleChanged)
            ResetImportedResources();

        _externalHandle = handle;
        _externalSemaphoreHandle = semaphoreHandle;
        _pixelSize = new PixelSize((int)width, (int)height);
        _imageFormat = imageFormat;
        Log.Info($"[GpuExternalImage] SetExternalImage handle=0x{handle.ToInt64():X} sem=0x{semaphoreHandle.ToInt64():X} size={width}x{height} format={imageFormat}", "Editor");
        if (_isAttached)
        {
            await EnsureImportedImageAsync();
        }
    }

    public async Task<bool> RefreshAsync(ulong waitValue, ulong signalValue)
    {
        if (_drawingSurface == null || _importedImage == null)
        {
            Log.Debug($"[GpuExternalImage] Refresh skipped surface={_drawingSurface != null} image={_importedImage != null}", "Editor");
            return false;
        }

        if (_importedSemaphore != null && _syncCapabilities.HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.TimelineSemaphores))
        {
            await _drawingSurface.UpdateWithTimelineSemaphoresAsync(_importedImage, _importedSemaphore, waitValue, _importedSemaphore, signalValue);
        }
        else if (_importedSemaphore != null && _syncCapabilities.HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.Semaphores))
        {
            await _drawingSurface.UpdateWithSemaphoresAsync(_importedImage, _importedSemaphore, _importedSemaphore);
        }
        else
        {
            Log.Warn($"[GpuExternalImage] No supported explicit sync path caps={_syncCapabilities}", "Editor");
            return false;
        }

        if (_surfaceVisual != null)
            await _surfaceVisual.Compositor.RequestCommitAsync();
        Log.Debug($"[GpuExternalImage] Refresh committed wait={waitValue} signal={signalValue}", "Editor");
        return true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        Log.Info("[GpuExternalImage] Attached to visual tree", "Editor");
        _ = EnsureImportedImageAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        Log.Info("[GpuExternalImage] Detached from visual tree", "Editor");
        ResetImportedResources();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
        {
            UpdateVisualSize(Bounds.Size);
            Log.Debug($"[GpuExternalImage] Bounds changed to {Bounds.Width:0.##}x{Bounds.Height:0.##}", "Editor");
            if (_isAttached && _importedImage == null && Bounds.Width > 0 && Bounds.Height > 0)
                _ = EnsureImportedImageAsync();
        }
    }

    private async Task EnsureImportedImageAsync()
    {
        if (!_isAttached || _externalHandle == IntPtr.Zero || Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            Log.Debug($"[GpuExternalImage] Import deferred attached={_isAttached} handle={_externalHandle != IntPtr.Zero} bounds={Bounds.Width:0.##}x{Bounds.Height:0.##}", "Editor");
            return;
        }

        await EnsureCompositionAsync();
        if (_gpuInterop == null)
        {
            Log.Warn("[GpuExternalImage] Composition GPU interop unavailable", "Editor");
            return;
        }

        if (_importedImage == null)
        {
            bool supportsIoSurface = _gpuInterop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef);
            Log.Info($"[GpuExternalImage] Supported handles: {string.Join(", ", _gpuInterop.SupportedImageHandleTypes)}", "Editor");
            if (!supportsIoSurface)
                Log.Warn("[GpuExternalImage] IOSurfaceRef not supported by compositor backend", "Editor");

            _syncCapabilities = _gpuInterop.GetSynchronizationCapabilities(KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef);
            Log.Info($"[GpuExternalImage] Sync capabilities: {_syncCapabilities}", "Editor");
            Log.Info($"[GpuExternalImage] Supported semaphores: {string.Join(", ", _gpuInterop.SupportedSemaphoreTypes)}", "Editor");

            var properties = new PlatformGraphicsExternalImageProperties
            {
                Width = _pixelSize.Width,
                Height = _pixelSize.Height,
                Format = _imageFormat,
                TopLeftOrigin = true,
            };

            var platformHandle = new PlatformHandle(_externalHandle, KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef);
            _importedImage = _gpuInterop.ImportImage(platformHandle, properties);
            Log.Info($"[GpuExternalImage] ImportImage completed imported={_importedImage != null}", "Editor");
            if (_externalSemaphoreHandle != IntPtr.Zero &&
                _gpuInterop.SupportedSemaphoreTypes.Contains(KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent))
            {
                var semaphoreHandle = new PlatformHandle(_externalSemaphoreHandle, KnownPlatformGraphicsExternalSemaphoreHandleTypes.MetalSharedEvent);
                _importedSemaphore = _gpuInterop.ImportSemaphore(semaphoreHandle);
                Log.Info($"[GpuExternalImage] ImportSemaphore completed imported={_importedSemaphore != null}", "Editor");
            }
        }
    }

    public void ResetExternalImage()
    {
        _externalHandle = IntPtr.Zero;
        _externalSemaphoreHandle = IntPtr.Zero;
        ResetImportedResources();
    }

    private async Task EnsureCompositionAsync()
    {
        if (_compositionReady)
            return;

        var visual = ElementComposition.GetElementVisual(this);
        if (visual == null)
            return;

        var compositor = visual.Compositor;
        _gpuInterop = await compositor.TryGetCompositionGpuInterop();
        if (_gpuInterop == null)
        {
            Log.Warn("[GpuExternalImage] TryGetCompositionGpuInterop returned null", "Editor");
            return;
        }

        _drawingSurface = compositor.CreateDrawingSurface();
        _surfaceVisual = compositor.CreateSurfaceVisual();
        _surfaceVisual.Surface = _drawingSurface;
        UpdateVisualSize(Bounds.Size);
        ElementComposition.SetElementChildVisual(this, _surfaceVisual);
        _compositionReady = true;
        await compositor.RequestCommitAsync();
        Log.Info("[GpuExternalImage] Composition surface initialized", "Editor");
    }

    private void UpdateVisualSize(Size size)
    {
        if (_surfaceVisual == null)
            return;

        _surfaceVisual.Size = new Vector2((float)size.Width, (float)size.Height);
        _ = _surfaceVisual.Compositor.RequestCommitAsync();
    }

    private void ResetImportedResources()
    {
        (_importedSemaphore as IDisposable)?.Dispose();
        _importedSemaphore = null;
        (_importedImage as IDisposable)?.Dispose();
        _importedImage = null;
    }
}
