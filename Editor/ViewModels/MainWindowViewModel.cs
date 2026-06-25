// SPDX-License-Identifier: MIT
using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Engine.Editor.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private string _statusText = "Engine starting...";

    public ConsolePanelViewModel ConsoleVm { get; } = new();
}
