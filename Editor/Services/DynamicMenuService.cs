// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;

namespace Engine.Editor.Services;

/// <summary>
/// Routes registrations from editor-kind managed plugins into the Avalonia
/// menu/overlay/panel surfaces. See docs/editor/extensions.md for the
/// plugin contract and the Tools &gt; Extensions submenu convention.
/// </summary>
public sealed class DynamicMenuService
{
    public static DynamicMenuService Shared { get; } = new();

    private readonly Dictionary<string, List<MenuActionRegistration>> _menus = new();
    private readonly Dictionary<string, List<Action>> _imguiOverlays = new();
    private readonly Dictionary<string, List<object>> _toolPanels = new();

    /// <summary>Fired on the UI thread whenever a plugin adds or removes a menu action.</summary>
    public event Action? OnMenusChanged;
    /// <summary>Fired on the UI thread whenever a plugin adds or removes an ImGui overlay.</summary>
    public event Action? OnImGuiOverlaysChanged;
    /// <summary>Fired on the UI thread whenever a plugin adds or removes a tool panel.</summary>
    public event Action? OnToolPanelsChanged;

    /// <summary>One menu action registered by an editor-kind plugin.</summary>
    public sealed record MenuActionRegistration(string MenuPath, string ItemName, Action OnExecute);

    /// <summary>Registers a menu action under the plugin's namespace.</summary>
    public void RegisterMenuAction(string pluginId, string menuPath, string itemName, Action onExecute)
    {
        if (!_menus.ContainsKey(pluginId))
            _menus[pluginId] = new();

        _menus[pluginId].Add(new MenuActionRegistration(menuPath, itemName, onExecute));
        OnMenusChanged?.Invoke();
    }

    /// <summary>Registers an ImGui draw callback owned by an editor plugin.</summary>
    public void RegisterImGuiOverlay(string pluginId, Action onDraw)
    {
        if (!_imguiOverlays.ContainsKey(pluginId))
            _imguiOverlays[pluginId] = new();

        _imguiOverlays[pluginId].Add(onDraw);
        OnImGuiOverlaysChanged?.Invoke();
    }

    /// <summary>Registers a tool panel (any Avalonia Control) owned by an editor plugin.</summary>
    public void RegisterToolPanel(string pluginId, string title, object avaloniaControl)
    {
        if (!_toolPanels.ContainsKey(pluginId))
            _toolPanels[pluginId] = new();

        _toolPanels[pluginId].Add(new { Title = title, Control = avaloniaControl });
        OnToolPanelsChanged?.Invoke();
    }

    /// <summary>Removes every registration owned by the named plugin.</summary>
    public void UnregisterPlugin(string pluginId)
    {
        bool changedMenus = _menus.Remove(pluginId);
        bool changedImGui = _imguiOverlays.Remove(pluginId);
        bool changedPanels = _toolPanels.Remove(pluginId);

        if (changedMenus) OnMenusChanged?.Invoke();
        if (changedImGui) OnImGuiOverlaysChanged?.Invoke();
        if (changedPanels) OnToolPanelsChanged?.Invoke();
    }

    /// <summary>Enumerates every menu-action registration, in registration order.</summary>
    public IEnumerable<RegisteredMenuAction> EnumerateMenuActions()
    {
        foreach (var (pluginId, entries) in _menus)
        {
            foreach (var entry in entries)
            {
                yield return new RegisteredMenuAction(
                    pluginId,
                    entry.MenuPath,
                    entry.ItemName,
                    entry.OnExecute);
            }
        }
    }

    /// <summary>Flattened menu-action record pairing plugin ownership with the registration.</summary>
    public readonly record struct RegisteredMenuAction(
        string PluginId,
        string MenuPath,
        string ItemName,
        Action OnExecute);

    /// <summary>Enumerates every ImGui overlay callback, in registration order.</summary>
    public IEnumerable<Action> GetImGuiOverlays()
    {
        foreach (var pluginOverlays in _imguiOverlays.Values)
        {
            foreach (var overlay in pluginOverlays)
            {
                yield return overlay;
            }
        }
    }
}
