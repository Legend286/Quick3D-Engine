// SPDX-License-Identifier: MIT
using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Engine.Editor.ViewModels;

namespace Engine.Editor.Views;

public partial class ConsolePanelView : UserControl
{
    public ConsolePanelView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ConsolePanelViewModel vm)
        {
            vm.ScrollToEndRequested += OnScrollToEndRequested;
        }
    }

    private void OnScrollToEndRequested()
    {
        var listBox = this.FindControl<ListBox>("LogListBox");
        if (listBox is not null && listBox.ItemCount > 0)
        {
            listBox.ScrollIntoView(listBox.ItemCount - 1);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnClearClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConsolePanelViewModel vm)
            vm.Clear();
    }

    private void OnFilterAllClicked(object? sender, RoutedEventArgs e)
        => (DataContext as ConsolePanelViewModel)?.SetFilter(-1);

    private void OnFilterErrorClicked(object? sender, RoutedEventArgs e)
        => (DataContext as ConsolePanelViewModel)?.SetFilter(Engine.CBindings.EngineLog.EngineLogError);

    private void OnFilterWarnClicked(object? sender, RoutedEventArgs e)
        => (DataContext as ConsolePanelViewModel)?.SetFilter(Engine.CBindings.EngineLog.EngineLogWarn);

    private void OnFilterInfoClicked(object? sender, RoutedEventArgs e)
        => (DataContext as ConsolePanelViewModel)?.SetFilter(Engine.CBindings.EngineLog.EngineLogInfo);

    private void OnFilterDebugClicked(object? sender, RoutedEventArgs e)
        => (DataContext as ConsolePanelViewModel)?.SetFilter(Engine.CBindings.EngineLog.EngineLogDebug);

    private void OnFilterTraceClicked(object? sender, RoutedEventArgs e)
        => (DataContext as ConsolePanelViewModel)?.SetFilter(Engine.CBindings.EngineLog.EngineLogTrace);

    private void OnSourceClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ConsoleEntryViewModel entry && entry.HasSource)
        {
            OpenInEditor(entry.SourceFilePath, entry.SourceLine, entry.SourceColumn);
        }
    }

    /// <summary>
    /// Opens the source file at the given line in VS Code (or the system default
    /// editor). Uses the 'code' CLI if available on PATH; falls back to platform
    /// open.
    /// </summary>
    private static void OpenInEditor(string filePath, int line, int column)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "code",
                Arguments = $"--goto \"{filePath}:{line}:{column}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);
        }
        catch
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch { }
        }
    }

    private void OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == Avalonia.Input.Key.L && e.KeyModifiers == Avalonia.Input.KeyModifiers.Control)
        {
            (DataContext as ConsolePanelViewModel)?.Clear();
            e.Handled = true;
        }
    }
}
