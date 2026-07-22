// SPDX-License-Identifier: MIT
using Avalonia.Controls;
using Avalonia.Input;
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
        TextInput += OnTextInput;
        PointerWheelChanged += OnPointerWheelChanged;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
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

            int btn = props.PointerUpdateKind switch
            {
                Avalonia.Input.PointerUpdateKind.LeftButtonPressed => 0,
                Avalonia.Input.PointerUpdateKind.RightButtonPressed => 1,
                Avalonia.Input.PointerUpdateKind.MiddleButtonPressed => 2,
                _ => -1
            };
            if (btn != -1) vm.QueueMouseButtonEvent(btn, true);
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

            int btn = props.PointerUpdateKind switch
            {
                Avalonia.Input.PointerUpdateKind.LeftButtonReleased => 0,
                Avalonia.Input.PointerUpdateKind.RightButtonReleased => 1,
                Avalonia.Input.PointerUpdateKind.MiddleButtonReleased => 2,
                _ => -1
            };
            if (btn != -1) vm.QueueMouseButtonEvent(btn, false);
        }

        if (props.PointerUpdateKind == Avalonia.Input.PointerUpdateKind.RightButtonReleased)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is ViewportPanelViewModel vm && e.Data.GetFiles() is { } files)
        {
            var pos = e.GetPosition(this);
            uint x = (uint)pos.X;
            uint y = (uint)pos.Y;
            uint w = (uint)System.Math.Max(1, Bounds.Width);
            uint h = (uint)System.Math.Max(1, Bounds.Height);

            foreach (var file in files)
            {
                if (file.Path.LocalPath is string path)
                {
                    string ext = System.IO.Path.GetExtension(path).ToLower();
                    if (ext == ".mdl" || path.EndsWith(".scene.json"))
                    {
                        Engine.CBindings.Log.Info($"Dropped Model/Scene: {path}", "Editor");
                        vm.InstantiateModel(path);
                    }
                    else if (ext == ".mat")
                    {
                        Engine.CBindings.Log.Info($"Dropped Material at ({x}, {y}) onto viewport: {path}", "Editor");
                        vm.GameLoop?.ApplyMaterialToSubmesh(x, y, w, h, path);
                    }
                }
            }
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

    private void OnTextInput(object? sender, Avalonia.Input.TextInputEventArgs e)
    {
        if (DataContext is ViewportPanelViewModel vm && !string.IsNullOrEmpty(e.Text))
        {
            foreach (var c in e.Text)
                vm.QueueCharEvent(c);
        }
    }

    private void OnPointerWheelChanged(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        if (DataContext is ViewportPanelViewModel vm)
        {
            vm.QueueScrollEvent((float)e.Delta.X, (float)e.Delta.Y);
        }
    }
}
