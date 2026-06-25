// SPDX-License-Identifier: MIT
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Engine.Editor.Views;

public partial class ViewportPanelView : UserControl
{
    public ViewportPanelView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
