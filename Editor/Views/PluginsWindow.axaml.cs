// SPDX-License-Identifier: MIT
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Engine.Editor.Services;

namespace Engine.Editor.Views;

/// <summary>Displays engine plugins enabled for the active project.</summary>
public partial class PluginsWindow : Window
{
    /// <summary>Creates the plugin manager window.</summary>
    public PluginsWindow()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = PluginCatalogService.Shared;
    }
}
