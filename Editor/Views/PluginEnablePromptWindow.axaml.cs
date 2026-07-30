// SPDX-License-Identifier: MIT
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Engine.Editor.Views;

/// <summary>Prompts for enabling an unavailable renderer plugin.</summary>
public partial class PluginEnablePromptWindow : Window
{
    /// <summary>Creates the renderer-plugin prompt.</summary>
    public PluginEnablePromptWindow()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnCancelClicked(
        object? sender,
        RoutedEventArgs e)
        => Close(PluginEnablePromptResult.Cancel);

    private void OnOpenPluginsClicked(
        object? sender,
        RoutedEventArgs e)
        => Close(
            PluginEnablePromptResult.OpenPlugins);

    private void OnEnableClicked(
        object? sender,
        RoutedEventArgs e)
        => Close(PluginEnablePromptResult.Enable);
}

/// <summary>Identifies the action selected by the plugin prompt.</summary>
public enum PluginEnablePromptResult
{
    /// <summary>Leaves the plugin disabled.</summary>
    Cancel,

    /// <summary>Opens the plugin manager.</summary>
    OpenPlugins,

    /// <summary>Enables the requested plugin.</summary>
    Enable
}
