// SPDX-License-Identifier: MIT
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Engine.Editor.ViewModels;

namespace Engine.Editor.Views;

public partial class ViewportPanelView : UserControl
{
    private readonly ViewportMetalLayerHost? _metalHost;

    public ViewportPanelView()
    {
        InitializeComponent();
        _metalHost =
            this.FindControl<ViewportMetalLayerHost>("MetalHost");

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
    private bool _viewportInteractionActive;
    private Avalonia.Point _lastPoint;

    private void OnPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (_metalHost is null)
            return;
        var p = e.GetPosition(_metalHost);
        if (!IsInsideViewport(p))
            return;

        var props = e.GetCurrentPoint(_metalHost).Properties;
        _viewportInteractionActive = true;
        if (DataContext is ViewportPanelViewModel vm)
        {
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
            _lastPoint = p;
            e.Pointer.Capture(this);
            Focus();
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_metalHost is null)
            return;
        var p = e.GetPosition(_metalHost);
        var props = e.GetCurrentPoint(_metalHost).Properties;
        bool insideViewport = IsInsideViewport(p);
        if (DataContext is ViewportPanelViewModel vm)
        {
            bool forwardButtons =
                insideViewport ||
                _viewportInteractionActive;
            vm.UpdatePointerState(
                (float)p.X,
                (float)p.Y,
                forwardButtons && props.IsLeftButtonPressed,
                forwardButtons && props.IsRightButtonPressed,
                forwardButtons && props.IsMiddleButtonPressed);

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
        if (_metalHost is null)
            return;
        var p = e.GetPosition(_metalHost);
        var props = e.GetCurrentPoint(_metalHost).Properties;
        if (DataContext is ViewportPanelViewModel vm)
        {
            vm.UpdatePointerState((float)p.X, (float)p.Y, props.IsLeftButtonPressed, props.IsRightButtonPressed, props.IsMiddleButtonPressed);

            int btn = props.PointerUpdateKind switch
            {
                Avalonia.Input.PointerUpdateKind.LeftButtonReleased => 0,
                Avalonia.Input.PointerUpdateKind.RightButtonReleased => 1,
                Avalonia.Input.PointerUpdateKind.MiddleButtonReleased => 2,
                _ => -1
            };
            if (btn != -1 && _viewportInteractionActive)
                vm.QueueMouseButtonEvent(btn, false);
        }

        if (props.PointerUpdateKind == Avalonia.Input.PointerUpdateKind.RightButtonReleased)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
        if (!props.IsLeftButtonPressed &&
            !props.IsRightButtonPressed &&
            !props.IsMiddleButtonPressed)
        {
            _viewportInteractionActive = false;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ViewportPanelViewModel vm)
            return;

        AssetDragPayload? assetPayload =
            e.DataTransfer.TryGetValue(AssetDragData.Format);
        if (assetPayload != null)
        {
            ApplyDroppedPath(
                vm,
                assetPayload.AssetPath,
                e,
                assetPayload.ModelPartIndex);
            return;
        }

        var files = e.DataTransfer.TryGetFiles();
        if (files == null)
        {
            var textPath = e.DataTransfer.TryGetText();
            if (!string.IsNullOrWhiteSpace(textPath))
            {
                ApplyDroppedPath(vm, textPath, e);
            }
            return;
        }

        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is string path)
            {
                ApplyDroppedPath(vm, path, e);
            }
        }
    }

    private void ApplyDroppedPath(
        ViewportPanelViewModel vm,
        string path,
        DragEventArgs e,
        int modelPartIndex = -1)
    {
        if (_metalHost is null)
            return;
        var pos = e.GetPosition(_metalHost);
        if (!IsInsideViewport(pos))
            return;
        double scale =
            TopLevel.GetTopLevel(_metalHost)?.RenderScaling ?? 1.0;
        uint w = (uint)System.Math.Max(
            1,
            System.Math.Round(_metalHost.Bounds.Width * scale));
        uint h = (uint)System.Math.Max(
            1,
            System.Math.Round(_metalHost.Bounds.Height * scale));
        uint x = (uint)System.Math.Clamp(
            System.Math.Round(pos.X * scale),
            0,
            w - 1);
        uint y = (uint)System.Math.Clamp(
            System.Math.Round(pos.Y * scale),
            0,
            h - 1);

        string ext = System.IO.Path.GetExtension(path).ToLower();
        if (ext == ".mdl" || path.EndsWith(".scene.json"))
        {
            Engine.CBindings.Log.Info($"Dropped Model/Scene: {path}", "Editor");
            vm.InstantiateModel(path, modelPartIndex);
        }
        else if (ext == ".mat")
        {
            Engine.CBindings.Log.Info($"Dropped Material at ({x}, {y}) onto viewport: {path}", "Editor");
            vm.GameLoop?.ApplyMaterialToSubmesh(x, y, w, h, path);
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

    private void OnDebugViewSelectionChanged(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0)
            return;
        this.FindControl<Button>("DebugViewButton")?.Flyout?.Hide();
        Focus();
    }

    private void OnPointerWheelChanged(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        if (_metalHost is not null &&
            IsInsideViewport(e.GetPosition(_metalHost)) &&
            DataContext is ViewportPanelViewModel vm)
        {
            vm.QueueScrollEvent((float)e.Delta.X, (float)e.Delta.Y);
        }
    }

    private bool IsInsideViewport(Avalonia.Point point)
    {
        return _metalHost is not null &&
            point.X >= 0 &&
            point.Y >= 0 &&
            point.X < _metalHost.Bounds.Width &&
            point.Y < _metalHost.Bounds.Height;
    }
}
