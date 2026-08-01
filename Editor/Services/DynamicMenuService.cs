// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.ComponentModel;

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
    private readonly Dictionary<string, List<DebugViewRegistration>> _debugViews = new();
    private readonly Dictionary<string, List<DebugViewToggleRegistration>> _debugViewToggles = new();
    private string _activeDebugView = "Lit";

    /// <summary>Fired on the UI thread whenever a plugin adds or removes a menu action.</summary>
    public event Action? OnMenusChanged;
    /// <summary>Fired on the UI thread whenever a plugin adds or removes an ImGui overlay.</summary>
    public event Action? OnImGuiOverlaysChanged;
    /// <summary>Fired on the UI thread whenever a plugin adds or removes a tool panel.</summary>
    public event Action? OnToolPanelsChanged;
    /// <summary>Fired on the UI thread whenever a plugin adds or removes a debug view.</summary>
    public event Action? OnDebugViewsChanged;
    /// <summary>Fired whenever a plugin adds or removes a debug-view toggle.</summary>
    public event Action? OnDebugViewTogglesChanged;

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

    /// <summary>One debug-view registration owned by a plugin. The
    /// <see cref="OnToggle"/> callback receives <c>true</c> when the
    /// view is selected and <c>false</c> when it is deselected.</summary>
    public sealed record DebugViewRegistration(
        string ViewName,
        Action<bool> OnToggle);

    /// <summary>One checkbox attached to a plugin-owned debug view.</summary>
    public sealed class DebugViewToggleRegistration : INotifyPropertyChanged
    {
        private readonly Action<bool> _onToggle;
        private bool _isChecked;

        internal DebugViewToggleRegistration(
            string viewName,
            string toggleName,
            bool initialValue,
            Action<bool> onToggle)
        {
            ViewName = viewName;
            ToggleName = toggleName;
            _isChecked = initialValue;
            _onToggle = onToggle;
        }

        /// <summary>Gets the debug view that owns this toggle.</summary>
        public string ViewName { get; }

        /// <summary>Gets the checkbox label.</summary>
        public string ToggleName { get; }

        /// <summary>Gets or sets the checkbox state.</summary>
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value)
                    return;
                _isChecked = value;
                _onToggle(value);
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        /// <inheritdoc />
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Registers a debug view (viewport visualization dropdown
    /// entry) owned by the named plugin.</summary>
    public void RegisterDebugView(
        string pluginId,
        string viewName,
        Action<bool> onToggle)
    {
        if (!_debugViews.ContainsKey(pluginId))
            _debugViews[pluginId] = new();

        _debugViews[pluginId].Add(
            new DebugViewRegistration(viewName, onToggle));
        onToggle(string.Equals(
            _activeDebugView,
            viewName,
            StringComparison.Ordinal));
        OnDebugViewsChanged?.Invoke();
    }

    /// <summary>Updates the active viewport visualization and synchronizes
    /// every plugin callback, including registrations recreated by reload.</summary>
    public void SetActiveDebugView(string viewName)
    {
        if (string.IsNullOrWhiteSpace(viewName))
            return;
        _activeDebugView = viewName;
        foreach (DebugViewRegistration view in EnumerateDebugViews())
        {
            view.OnToggle(string.Equals(
                viewName,
                view.ViewName,
                StringComparison.Ordinal));
        }
    }

    /// <summary>Registers a checkbox displayed for a named debug view.</summary>
    public void RegisterDebugViewToggle(
        string pluginId,
        string viewName,
        string toggleName,
        bool initialValue,
        Action<bool> onToggle)
    {
        if (!_debugViewToggles.ContainsKey(pluginId))
            _debugViewToggles[pluginId] = new();

        _debugViewToggles[pluginId].Add(
            new DebugViewToggleRegistration(
                viewName,
                toggleName,
                initialValue,
                value =>
                {
                    onToggle(value);
                    OnDebugViewTogglesChanged?.Invoke();
                }));
        OnDebugViewTogglesChanged?.Invoke();
    }

    /// <summary>Enumerates every debug-view registration, in registration order.</summary>
    public IEnumerable<DebugViewRegistration> EnumerateDebugViews()
    {
        foreach (var pluginViews in _debugViews.Values)
        {
            foreach (var view in pluginViews)
            {
                yield return view;
            }
        }
    }

    /// <summary>Enumerates every checkbox registered for a debug view.</summary>
    public IEnumerable<DebugViewToggleRegistration> EnumerateDebugViewToggles()
    {
        foreach (var pluginToggles in _debugViewToggles.Values)
        {
            foreach (var toggle in pluginToggles)
                yield return toggle;
        }
    }

    /// <summary>Removes every registration owned by the named plugin.</summary>
    public void UnregisterPlugin(string pluginId)
    {
        bool changedMenus = _menus.Remove(pluginId);
        bool changedImGui = _imguiOverlays.Remove(pluginId);
        bool changedPanels = _toolPanels.Remove(pluginId);
        bool changedViews = _debugViews.Remove(pluginId);
        bool changedViewToggles = _debugViewToggles.Remove(pluginId);

        if (changedMenus) OnMenusChanged?.Invoke();
        if (changedImGui) OnImGuiOverlaysChanged?.Invoke();
        if (changedPanels) OnToolPanelsChanged?.Invoke();
        if (changedViews) OnDebugViewsChanged?.Invoke();
        if (changedViewToggles) OnDebugViewTogglesChanged?.Invoke();
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
