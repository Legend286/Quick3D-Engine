// SPDX-License-Identifier: MIT
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Engine.Editor.Views;

public partial class ViewportPanelView : UserControl
{
    private bool _attached;

    public ViewportPanelView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_attached) return;
            if (e.NewValue is ViewModels.ViewportPanelViewModel vm)
            {
                _attached = true;
                vm.AttachToVisualTree(this);
            }
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
