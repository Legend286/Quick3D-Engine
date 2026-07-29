// SPDX-License-Identifier: MIT
using Avalonia.Controls;
using Avalonia.Input;
using Engine.Editor.ViewModels;

namespace Engine.Editor.Views;

public partial class RenderGraphExplorerView : UserControl
{
    public RenderGraphExplorerView()
    {
        InitializeComponent();
    }

    private void OnShadowFacePressed(
        object? sender,
        PointerPressedEventArgs e)
    {
        if (sender is not Border
            {
                DataContext:
                    RenderGraphShadowFaceRowViewModel row,
            } ||
            DataContext is not RenderGraphExplorerViewModel viewModel)
        {
            return;
        }
        viewModel.ShowShadowInspector(
            row,
            TopLevel.GetTopLevel(this) as Window);
        e.Handled = true;
    }
}
