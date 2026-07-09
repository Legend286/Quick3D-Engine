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

    private async void OnAssetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && sender is Control control)
        {
            if (control.DataContext is ContentAsset asset)
            {
                var dragData = new DataObject();
                dragData.Set(DataFormats.FileNames, new[] { asset.FullPath });
                dragData.Set("AssetType", asset.AssetType);

                await DragDrop.DoDragDrop(e, dragData, DragDropEffects.Copy | DragDropEffects.Link);
            }
        }
    }
}
