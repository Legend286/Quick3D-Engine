// SPDX-License-Identifier: MIT
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Interactivity;
using Engine.Editor.ViewModels;

namespace Engine.Editor.Views;

public partial class ConsolePanelView : UserControl
{
    public ConsolePanelView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClearClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConsolePanelViewModel vm)
            vm.Entries.Clear();
    }
}
