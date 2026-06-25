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
        // Release the Metal swapchain + device before tearing down the logger.
        if (DataContext is MainWindowViewModel vm)
        {
            vm.ViewportVm?.DisposeOnClose();
            vm.ConsoleVm?.DisposeOnClose();
        }
        EngineLog.EngineLogShutdown();
        base.OnClosed(e);
    }
}
