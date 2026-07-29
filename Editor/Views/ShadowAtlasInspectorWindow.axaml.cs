// SPDX-License-Identifier: MIT

using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Engine.CBindings;
using Engine.Editor.ViewModels;
using Engine.RHI;

namespace Engine.Editor.Views;

public partial class ShadowAtlasInspectorWindow : Window
{
    private const uint PreviewSize = 512;
    private readonly DispatcherTimer _timer;
    private ViewportPanelViewModel? _viewport;
    private RenderGraphShadowFaceDiagnostics? _face;
    private RhiTexture? _target;
    private RhiFence? _fence;
    private IntPtr _externalImageHandle;
    private IntPtr _externalSemaphoreHandle;
    private ulong _waitValue;
    private ulong _signalValue = 1;
    private bool _dynamicTile;
    private bool _refreshing;

    public ShadowAtlasInspectorWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(125),
        };
        _timer.Tick += OnTimerTick;
        Closed += OnClosed;
    }

    public async void ShowTile(
        ViewportPanelViewModel viewport,
        RenderGraphShadowFaceDiagnostics face,
        Window? owner)
    {
        _viewport = viewport;
        _face = face;
        _dynamicTile = false;
        UpdateLabels();
        if (!IsVisible)
        {
            if (owner != null)
                Show(owner);
            else
                Show();
        }

        if (!EnsureTarget())
            return;
        var exported = _target!.ExportExternalImage();
        var exportedSemaphore = _fence!.ExportExternalHandle();
        ReleaseExternalHandles();
        _externalImageHandle = exported.Handle;
        _externalSemaphoreHandle = exportedSemaphore.Handle;
        await PreviewControl.SetExternalImageAsync(
            exported.Handle,
            exported.Width,
            exported.Height,
            PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
            exportedSemaphore.Handle);
        await RefreshPreviewAsync();
        _timer.Start();
    }

    private bool EnsureTarget()
    {
        if (_target != null && _fence != null)
            return true;
        if (_viewport?.Device == null)
            return false;
        _target = RhiTexture.CreateExternalRenderTarget(
            _viewport.Device,
            PreviewSize,
            PreviewSize,
            RhiNative.TextureFormat.Bgra8Unorm);
        _target.SetDebugName(
            "Shadow Atlas Inspector",
            "Editor Preview");
        _fence = new RhiFence(_viewport.Device);
        _waitValue = 0;
        _signalValue = 1;
        return true;
    }

    private async void OnTimerTick(
        object? sender,
        EventArgs e)
    {
        await RefreshPreviewAsync();
    }

    private async System.Threading.Tasks.Task RefreshPreviewAsync()
    {
        if (_refreshing ||
            _viewport?.GameLoop == null ||
            _face == null ||
            _target == null ||
            _fence == null)
        {
            return;
        }

        _refreshing = true;
        try
        {
            bool rendered =
                _viewport.GameLoop.RenderShadowAtlasTilePreview(
                    _face.EntityId,
                    _face.FaceIndex,
                    _dynamicTile,
                    _target,
                    PreviewSize,
                    PreviewSize,
                    _fence,
                    _waitValue,
                    _signalValue);
            if (!rendered)
            {
                StateText.Text = "Tile is no longer resident";
                return;
            }
            bool refreshed = await PreviewControl.RefreshAsync(
                _signalValue,
                _signalValue + 1);
            if (refreshed)
            {
                _waitValue = _signalValue + 1;
                _signalValue = _waitValue + 1;
                StateText.Text = _dynamicTile
                    ? "Movable caster tile"
                    : "Static caster tile";
            }
        }
        catch (Exception ex)
        {
            StateText.Text = "Preview unavailable";
            Log.Error(
                $"[ShadowAtlasInspector] {ex}",
                "Editor");
            _timer.Stop();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void OnStaticClicked(
        object? sender,
        RoutedEventArgs e)
    {
        _dynamicTile = false;
        UpdateLabels();
        _ = RefreshPreviewAsync();
    }

    private void OnDynamicClicked(
        object? sender,
        RoutedEventArgs e)
    {
        _dynamicTile = true;
        UpdateLabels();
        _ = RefreshPreviewAsync();
    }

    private void UpdateLabels()
    {
        if (_face == null)
            return;
        TitleText.Text =
            $"Light {_face.LightIndex} · Face {_face.FaceIndex}";
        int page = _dynamicTile
            ? _face.DynamicPageIndex
            : _face.StaticPageIndex;
        int slot = _dynamicTile
            ? _face.DynamicSlotIndex
            : _face.StaticSlotIndex;
        AllocationText.Text =
            $"entity {_face.EntityId}  page {page}  slot {slot}  " +
            $"{_face.TileX},{_face.TileY}  {_face.TileSize}×{_face.TileSize}";
        StaticButton.Opacity = _dynamicTile ? 0.55 : 1.0;
        DynamicButton.Opacity = _dynamicTile ? 1.0 : 0.55;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        PreviewControl.ResetExternalImage();
        ReleaseExternalHandles();
        _fence?.Dispose();
        _fence = null;
        _target?.Dispose();
        _target = null;
    }

    private void ReleaseExternalHandles()
    {
        if (_externalImageHandle != IntPtr.Zero)
        {
            RhiTexture.ReleaseExternalImageHandle(
                _externalImageHandle);
            _externalImageHandle = IntPtr.Zero;
        }
        if (_externalSemaphoreHandle != IntPtr.Zero)
        {
            RhiFence.ReleaseExternalSemaphoreHandle(
                _externalSemaphoreHandle);
            _externalSemaphoreHandle = IntPtr.Zero;
        }
    }
}
