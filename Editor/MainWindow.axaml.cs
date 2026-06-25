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
        Opened += OnOpened;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Window.Opened fires once, after the window is fully shown AND its
    // children have been laid out. This is the first moment the host Window
    // resolves via TopLevel.GetTopLevel - earlier lifecycle hooks on the
    // ViewportPanelView ran before the visual subtree connected and Metal
    // init aborted with 'Viewport host is not a Window'.
    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.ViewportVm is not null)
            vm.ViewportVm.AttachToVisualTree(this);
    }

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
