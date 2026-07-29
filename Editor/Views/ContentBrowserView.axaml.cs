// SPDX-License-Identifier: MIT
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Engine.Editor.ViewModels;

namespace Engine.Editor.Views;

public partial class ContentBrowserView : UserControl
{
    private const double HoverPreviewWidth = 360;
    private const double HoverPreviewHeight = 392;
    private const double HoverPreviewOffsetX = 18;
    private const double HoverPreviewOffsetY = 14;

    private ContentBrowserViewModel? _vm;
    private static readonly DataFormat<string> AssetPathFormat = DataFormat.CreateInProcessFormat<string>("quick3d.asset-path");
    private AssetHoverPopupWindow? _hoverPopupWindow;
    private PixelPoint _hoverPreviewScreenPosition;

    public ContentBrowserView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnFolderDragOver);
        AddHandler(DragDrop.DropEvent, OnFolderDrop);
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private Avalonia.Point _dragStartPoint;
    private bool _isDraggingReady;
    private ContentAsset? _dragAsset;
    private ContentFolder? _dragFolder;
    private PointerPressedEventArgs? _dragStartEvent;

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;

        _vm = DataContext as ContentBrowserViewModel;
        if (_vm != null)
            _vm.PropertyChanged += OnViewModelPropertyChanged;
        base.OnDataContextChanged(e);
    }

    private void OnDetachedFromVisualTree(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        CloseHoverPopupWindow();
    }

    private void EnsureHoverPopupWindow()
    {
        if (_hoverPopupWindow != null)
            return;

        _hoverPopupWindow = new AssetHoverPopupWindow();
        _hoverPopupWindow.Opened += OnHoverPopupWindowOpened;
    }

    private void CloseHoverPopupWindow()
    {
        if (_hoverPopupWindow != null)
        {
            _hoverPopupWindow.Opened -= OnHoverPopupWindowOpened;
            _hoverPopupWindow.Close();
            _hoverPopupWindow = null;
        }
    }

    private void HideHoverPopupWindow()
    {
        if (_hoverPopupWindow?.IsVisible == true)
        {
            _hoverPopupWindow.StopLivePreview();
            _hoverPopupWindow.Hide();
        }
    }

    private void OnHoverPopupWindowOpened(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(SyncHoverPopupWindow, DispatcherPriority.Background);
    }

    private void SyncHoverPopupWindow()
    {
        var ownerWindow = TopLevel.GetTopLevel(this) as Window;
        if (_vm == null || !_vm.HoverPreviewVisible || ownerWindow == null)
        {
            HideHoverPopupWindow();
            return;
        }

        EnsureHoverPopupWindow();
        if (_hoverPopupWindow == null)
            return;

        UpdateHoverPopupWindowPosition();
        if (!_hoverPopupWindow.IsVisible)
        {
            _hoverPopupWindow.Show(ownerWindow);
            return;
        }

        MainWindowViewModel? mainVm = ownerWindow.DataContext as MainWindowViewModel;
        bool wantsLivePreview = (_vm.HoverPreviewAssetType == "Model" || _vm.HoverPreviewAssetType == "Material")
            && _vm.HoverPreviewAsset != null
            && mainVm?.ViewportVm != null;

        if (wantsLivePreview && mainVm?.ViewportVm != null)
        {
            _hoverPopupWindow.StartLivePreview(
                mainVm.ViewportVm,
                _vm.HoverPreviewAsset!.FullPath,
                _vm.HoverPreviewAssetType,
                _vm.HoverPreviewTitle,
                _vm.HoverPreviewBitmap);
        }
        else
        {
            _hoverPopupWindow.UpdatePreview(_vm.HoverPreviewBitmap, _vm.HoverPreviewTitle, _vm.HoverPreviewAssetType);
        }
    }

    private void UpdateHoverPopupWindowPosition()
    {
        var win = TopLevel.GetTopLevel(this) as Window;
        if (_hoverPopupWindow == null || win == null)
            return;

        const int width = 360;
        const int height = 392;
        const int margin = 8;
        var screen = win.Screens.ScreenFromWindow(win) ?? win.Screens.Primary;
        var bounds = screen?.WorkingArea ?? new PixelRect(0, 0, 1920, 1080);

        int left = _hoverPreviewScreenPosition.X;
        int top = _hoverPreviewScreenPosition.Y - height;
        if (top < bounds.Y + margin)
            top = _hoverPreviewScreenPosition.Y + 14;

        if (left + width > bounds.Right - margin)
            left = _hoverPreviewScreenPosition.X - width - 18;

        left = Math.Clamp(left, bounds.X + margin, bounds.Right - width - margin);
        top = Math.Clamp(top, bounds.Y + margin, bounds.Bottom - height - margin);
        _hoverPopupWindow.Position = new PixelPoint(left, top);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ContentBrowserViewModel.HoverPreviewVisible) ||
            e.PropertyName == nameof(ContentBrowserViewModel.HoverPreviewBitmap) ||
            e.PropertyName == nameof(ContentBrowserViewModel.HoverPreviewTitle) ||
            e.PropertyName == nameof(ContentBrowserViewModel.HoverPreviewAssetType) ||
            e.PropertyName == nameof(ContentBrowserViewModel.HoverPreviewAsset))
        {
            Dispatcher.UIThread.Post(SyncHoverPopupWindow, DispatcherPriority.Background);
        }
    }

    private void UpdateHoverPreviewPosition(PointerEventArgs e, Visual? sourceVisual = null)
    {
        var win = TopLevel.GetTopLevel(this) as Window;
        if (_vm == null || win == null)
            return;

        var point = e.GetPosition(win);
        double maxLeft = System.Math.Max(0, win.Bounds.Width - HoverPreviewWidth - 8);
        double maxTop = System.Math.Max(0, win.Bounds.Height - HoverPreviewHeight - 8);
        double preferredLeft = point.X + HoverPreviewOffsetX;
        double preferredTop = point.Y - HoverPreviewHeight - HoverPreviewOffsetY;

        if (preferredTop < 0)
            preferredTop = point.Y + HoverPreviewOffsetY;
        if (preferredLeft > maxLeft)
            preferredLeft = point.X - HoverPreviewWidth - HoverPreviewOffsetX;

        double left = System.Math.Clamp(preferredLeft, 0, maxLeft);
        double top = System.Math.Clamp(preferredTop, 0, maxTop);
        _vm.UpdateAssetHoverPosition(left, top);

        var anchorVisual = sourceVisual ?? win;
        var screenPoint = anchorVisual.PointToScreen(e.GetPosition(anchorVisual));
        _hoverPreviewScreenPosition = new PixelPoint(
            screenPoint.X + (int)Math.Round(HoverPreviewOffsetX),
            screenPoint.Y);

        if (_hoverPopupWindow?.IsVisible == true)
            UpdateHoverPopupWindowPosition();
    }

    private void OnFolderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && sender is Control control)
        {
            if (control.DataContext is ContentFolder folder)
            {
                _dragStartPoint = e.GetPosition(this);
                _isDraggingReady = true;
                _dragFolder = folder;
                _dragStartEvent = e;
            }
        }
    }

    private async void OnFolderPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingReady || _dragFolder == null) return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDraggingReady = false;
            _dragFolder = null;
            _dragStartEvent = null;
            return;
        }

        var currentPoint = e.GetPosition(this);
        var distance = System.Math.Sqrt(System.Math.Pow(currentPoint.X - _dragStartPoint.X, 2) + System.Math.Pow(currentPoint.Y - _dragStartPoint.Y, 2));

        if (distance > 3)
        {
            _isDraggingReady = false;
            var folder = _dragFolder;
            var dragStartEvent = _dragStartEvent;
            if (folder == null || dragStartEvent == null)
            {
                _dragFolder = null;
                _dragStartEvent = null;
                return;
            }

            var dragData = new DataTransfer();
            dragData.Add(DataTransferItem.CreateText(folder.FullPath));

            await DragDrop.DoDragDropAsync(dragStartEvent, dragData, DragDropEffects.Move);
            _dragFolder = null;
            _dragStartEvent = null;
        }
    }

    private void OnFolderDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Text))
            e.DragEffects = DragDropEffects.Move | DragDropEffects.Copy;
        else
            e.DragEffects = DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFolderDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is ContentBrowserViewModel vm && e.Source is Control control)
        {
            if (control.DataContext is ContentFolder targetFolder)
            {
                if (e.DataTransfer.Contains(DataFormat.File))
                {
                    var files = e.DataTransfer.TryGetFiles();
                    if (files != null)
                    {
                        foreach (var item in files)
                        {
                            if (item.TryGetLocalPath() is string sourcePath)
                            {
                                vm.MoveItem(sourcePath, targetFolder.FullPath);
                            }
                        }
                        e.Handled = true;
                    }
                }
                else
                {
                    var sourcePath = e.DataTransfer.TryGetText();
                    if (!string.IsNullOrWhiteSpace(sourcePath))
                    {
                        vm.MoveItem(sourcePath, targetFolder.FullPath);
                        e.Handled = true;
                    }
                }
            }
        }
    }


    private void OnAssetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && sender is Control control)
        {
            if (control.DataContext is ContentAsset asset)
            {
                _dragStartPoint = e.GetPosition(this);
                _isDraggingReady = true;
                _dragAsset = asset;
                _dragStartEvent = e;
            }
        }
    }

    private async void OnAssetPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is Control hoverControl && hoverControl.DataContext is ContentAsset hoverAsset && !_isDraggingReady)
        {
            UpdateHoverPreviewPosition(e, hoverControl);
        }

        if (!_isDraggingReady || _dragAsset == null) return;
        
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDraggingReady = false;
            _dragAsset = null;
            _dragStartEvent = null;
            return;
        }

        var currentPoint = e.GetPosition(this);
        var distance = System.Math.Sqrt(System.Math.Pow(currentPoint.X - _dragStartPoint.X, 2) + System.Math.Pow(currentPoint.Y - _dragStartPoint.Y, 2));

        if (distance > 3) // threshold
        {
            _isDraggingReady = false; // Prevent multiple drag starts
            _vm?.EndAssetHover(_dragAsset);
            var asset = _dragAsset;
            var dragStartEvent = _dragStartEvent;
            if (asset == null || dragStartEvent == null)
            {
                _dragAsset = null;
                _dragStartEvent = null;
                return;
            }

            var dragData = new DataTransfer();
            dragData.Add(DataTransferItem.CreateText(asset.FullPath));

            await DragDrop.DoDragDropAsync(dragStartEvent, dragData, DragDropEffects.Copy);
            
            _dragAsset = null;
            _dragStartEvent = null;
        }
    }

    private void OnAssetPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_isDraggingReady)
            return;
        if (DataContext is ContentBrowserViewModel vm && sender is Control control && control.DataContext is ContentAsset asset)
        {
            UpdateHoverPreviewPosition(e, control);
            vm.BeginAssetHover(asset, vm.HoverPreviewLeft, vm.HoverPreviewTop);
        }
    }

    private void OnAssetPointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is not ContentBrowserViewModel vm || sender is not Control control || control.DataContext is not ContentAsset asset)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (_hoverPopupWindow?.IsVisible == true)
                return;
            if (!control.IsPointerOver)
                vm.EndAssetHover(asset);
        }, DispatcherPriority.Background);
    }

    private void OnAssetPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDraggingReady = false;
        _dragAsset = null;
        _dragStartEvent = null;
    }

    private void OnAssetDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control control && control.DataContext is ContentAsset asset)
        {
            if (asset.AssetType == "Material")
            {
                var window = new MaterialEditorWindow(asset.FullPath);
                window.Show();
            }
        }
    }
}
