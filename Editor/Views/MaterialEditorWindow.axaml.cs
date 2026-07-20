// SPDX-License-Identifier: MIT
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Engine.Editor.ViewModels;

namespace Engine.Editor.Views;

public partial class MaterialEditorWindow : Window
{
    private MaterialEditorViewModel? _viewModel;

    public MaterialEditorWindow()
    {
        InitializeComponent();
    }

    public MaterialEditorWindow(string materialPath) : this()
    {
        var host = this.FindControl<ViewportMetalLayerHost>("PreviewHost");
        _viewModel = new MaterialEditorViewModel(materialPath);
        DataContext = _viewModel;

        Opened += (s, e) => {
            if (host != null) _viewModel.AttachToVisualTree(this, host);
        };

        Closing += (s, e) => {
            _viewModel.Dispose();
        };

        if (host != null)
        {
            host.PointerPressed += OnPointerPressed;
            host.PointerMoved += OnPointerMoved;
            host.PointerReleased += OnPointerReleased;
            host.PointerWheelChanged += OnPointerWheelChanged;
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private bool _isDragging;
    private Avalonia.Point _lastPoint;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            _isDragging = true;
            _lastPoint = e.GetPosition(this);
            e.Pointer.Capture(sender as IInputElement);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging && _viewModel != null)
        {
            var p = e.GetPosition(this);
            float dx = (float)(p.X - _lastPoint.X);
            float dy = (float)(p.Y - _lastPoint.Y);
            _lastPoint = p;
            _viewModel.AddPointerDelta(dx, dy);
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Right)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _viewModel?.AddScrollDelta((float)e.Delta.Y);
    }
}
