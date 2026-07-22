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
        AddHandler(DragDrop.DragOverEvent, OnFolderDragOver);
        AddHandler(DragDrop.DropEvent, OnFolderDrop);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private Avalonia.Point _dragStartPoint;
    private bool _isDraggingReady;
    private ContentAsset? _dragAsset;
    private ContentFolder? _dragFolder;

    private void OnFolderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && sender is Control control)
        {
            if (control.DataContext is ContentFolder folder)
            {
                _dragStartPoint = e.GetPosition(this);
                _isDraggingReady = true;
                _dragFolder = folder;
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
            return;
        }

        var currentPoint = e.GetPosition(this);
        var distance = System.Math.Sqrt(System.Math.Pow(currentPoint.X - _dragStartPoint.X, 2) + System.Math.Pow(currentPoint.Y - _dragStartPoint.Y, 2));

        if (distance > 3)
        {
            _isDraggingReady = false;
            var folder = _dragFolder;

            var dragData = new DataObject();
            dragData.Set(DataFormats.FileNames, new[] { folder.FullPath });
            dragData.Set("AssetType", "Folder");

            await DragDrop.DoDragDrop(e, dragData, DragDropEffects.Move);
            _dragFolder = null;
        }
    }

    private void OnFolderDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.FileNames))
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
                if (e.Data.Contains(DataFormats.Files))
                {
                    var files = e.Data.GetFiles();
                    if (files != null)
                    {
                        foreach (var item in files)
                        {
                            if (item.Path.LocalPath is string sourcePath)
                            {
                                vm.MoveItem(sourcePath, targetFolder.FullPath);
                            }
                        }
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
