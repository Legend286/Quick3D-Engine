// SPDX-License-Identifier: MIT
using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Engine.Editor.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = "Engine starting...";

    public ConsolePanelViewModel ConsoleVm { get; } = new();

    /// <summary>Bound to the central viewport panel. Owns the Metal swapchain
    /// + WriteableBitmap pipeline on macOS. Null on Windows until Phase 2
    /// Vulkan path lands.</summary>
    public ViewportPanelViewModel? ViewportVm { get; }

    public MainWindowViewModel()
    {
        if (OperatingSystem.IsMacOS())
        {
            string contentRoot = System.IO.Path.Combine(App.ProjectRoot, "Content");
            Console.WriteLine($"[MainWindowViewModel] ContentRoot: '{contentRoot}'");
            ViewportVm = new ViewportPanelViewModel(contentRoot: contentRoot, sceneName: "hello");
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
    }
}
