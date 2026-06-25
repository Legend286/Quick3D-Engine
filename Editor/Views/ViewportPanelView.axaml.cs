// SPDX-License-Identifier: MIT
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Engine.Editor.Views;

public partial class ViewportPanelView : UserControl
{
    private bool _attached;

    public ViewportPanelView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Avalonia 11 fires OnDataContextChanged(EventArgs) whenever a parent
    // XAML element sets our DataContext - the canonical hook for users
    // that bind their view-model via XAML. We read DataContext and dispatch
    // exactly once to the view-model's AttachToVisualTree. The 'attached'
    // guard protects against late-style reparenting if Avalonia re-fires
    // the hook during refresh.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_attached) return;
        if (DataContext is ViewModels.ViewportPanelViewModel vm)
        {
            _attached = true;
            vm.AttachToVisualTree(this);
        }
    }
}
