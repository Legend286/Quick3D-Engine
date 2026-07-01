// SPDX-License-Identifier: MIT
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Engine.Editor.ViewModels;

namespace Engine.Editor.Views;

public partial class ViewportPanelView : UserControl
{
    public ViewportPanelView()
    {
        InitializeComponent();
        
        Focusable = true;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private bool _isDragging;
    private Avalonia.Point _lastPoint;

    private void OnPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (DataContext is ViewportPanelViewModel vm)
        {
            var p = e.GetPosition(this);
            vm.UpdatePointerState((float)p.X, (float)p.Y, props.IsLeftButtonPressed, props.IsRightButtonPressed, props.IsMiddleButtonPressed);
        }

        if (props.IsRightButtonPressed)
        {
            _isDragging = true;
            _lastPoint = e.GetPosition(this);
            e.Pointer.Capture(this);
            Focus();
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (DataContext is ViewportPanelViewModel vm)
        {
            var p = e.GetPosition(this);
            vm.UpdatePointerState((float)p.X, (float)p.Y, props.IsLeftButtonPressed, props.IsRightButtonPressed, props.IsMiddleButtonPressed);
            
            if (_isDragging)
            {
                vm.AddPointerDelta((float)(p.X - _lastPoint.X), (float)(p.Y - _lastPoint.Y));
                _lastPoint = p;
                e.Handled = true;
            }
        }
    }

    private void OnPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (DataContext is ViewportPanelViewModel vm)
        {
            var p = e.GetPosition(this);
            vm.UpdatePointerState((float)p.X, (float)p.Y, props.IsLeftButtonPressed, props.IsRightButtonPressed, props.IsMiddleButtonPressed);
        }

        if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Right)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (DataContext is ViewportPanelViewModel vm)
            vm.SetKeyState(e.Key, true);
    }

    private void OnKeyUp(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (DataContext is ViewportPanelViewModel vm)
            vm.SetKeyState(e.Key, false);
    }
}
