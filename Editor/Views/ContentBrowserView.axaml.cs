// SPDX-License-Identifier: MIT
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Engine.Editor.ViewModels;

namespace Engine.Editor.Views;

public partial class ContentBrowserView : UserControl
{
    public ContentBrowserView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private Avalonia.Point _dragStartPoint;
    private bool _isDraggingReady;
    private ContentAsset? _dragAsset;

    private void OnAssetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && sender is Control control)
        {
            if (control.DataContext is ContentAsset asset)
            {
                _dragStartPoint = e.GetPosition(this);
                _isDraggingReady = true;
                _dragAsset = asset;
            }
        }
    }

    private async void OnAssetPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingReady || _dragAsset == null) return;
        
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDraggingReady = false;
            _dragAsset = null;
            return;
        }

        var currentPoint = e.GetPosition(this);
        var distance = System.Math.Sqrt(System.Math.Pow(currentPoint.X - _dragStartPoint.X, 2) + System.Math.Pow(currentPoint.Y - _dragStartPoint.Y, 2));

        if (distance > 3) // threshold
        {
            _isDraggingReady = false; // Prevent multiple drag starts
            var asset = _dragAsset;
            
            var dragData = new DataObject();
            dragData.Set(DataFormats.FileNames, new[] { asset.FullPath });
            dragData.Set("AssetType", asset.AssetType);

            await DragDrop.DoDragDrop(e, dragData, DragDropEffects.Copy | DragDropEffects.Link);
            
            _dragAsset = null;
        }
    }

    private void OnAssetPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _isDraggingReady = false;
        _dragAsset = null;
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
