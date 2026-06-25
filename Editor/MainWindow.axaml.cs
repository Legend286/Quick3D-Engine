// SPDX-License-Identifier: MIT
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Engine.CBindings;
using Engine.Editor.ViewModels;

namespace Engine.Editor;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(System.EventArgs e)
    {
        EngineLog.EngineLogShutdown();
        base.OnClosed(e);
    }
}
