// SPDX-License-Identifier: MIT
// ViewportPanelViewModel: owns the Metal swapchain on the Avalonia NSWindow,
// drives a 60 fps render loop, and pushes each frame's pixel readback into a
// WriteableBitmap bound to the Avalonia Image control.
//
// Resize handling: Avalonia's SizeChanged event bubbles up the user's window
// resize. We rebuild the swapchain + WriteableBitmap at the new dimensions.

using System;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Engine.CBindings;
using Engine.Game;
using Engine.RHI;
using Engine.Scene;
using Engine.RenderGraph;

namespace Engine.Editor.ViewModels;

public sealed class ViewportPanelViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer;
    private WriteableBitmap? _bitmap;
    private WriteableBitmap? _frame;
    private RhiDevice? _device;
    private RhiSwapchain? _swap;
    private Renderer? _renderer;
    private EcsWorld? _world;
    private IntPtr _nsWindow;
    private uint _width = 1;
    private uint _height = 1;
    private bool _attached;
    private bool _disposed;
    private readonly string _contentRoot;

    public ViewportPanelViewModel(string contentRoot, string sceneName = "hello")
    {
        _contentRoot = contentRoot;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
    }

    public WriteableBitmap? Frame
    {
        get => _frame;
        private set
        {
            if (_frame == value) return;
            _frame = value;
            OnPropertyChanged(nameof(Frame));
        }
    }

    public void AttachToVisualTree(Control host)
    {
        if (_attached) return;
        _attached = true;
        try
        {
            _nsWindow = AvaloniaNativeWindowInterop.GetMacOsWindowPointer(
                TopLevel.GetTopLevel(host) as Window
                ?? throw new InvalidOperationException("Viewport host is not a Window."));
            (uint w, uint h) = AvaloniaNativeWindowInterop.ReadFrameMetrics(
                TopLevel.GetTopLevel(host) as Window);
            _width  = w > 0 ? w : 1280;
            _height = h > 0 ? h : 720;
            _device = new RhiDevice();
            _swap   = _device.CreateSwapchain(_nsWindow, _width, _height);
            _world  = new EcsWorld();
            SeedTriangleEntity(_world);
            _renderer = new Renderer(_device, _swap, _world);
            _renderer.LoadScene(_contentRoot, "hello");
            AllocateBitmap();
            _timer.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[engine-viewport] init failed: {ex.Message}");
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_device is null || _swap is null || _renderer is null) return;
        if (!_swap.TryAcquireNextImage(out RhiTexture? image) || image is null)
            return;

        try
        {
            _renderer.RenderFrame(image, _width, _height);
            var bytes = image.Readback(_width, _height, _width * 4);
            PushBytesToBitmap(bytes);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[engine-viewport] frame failed: {ex.Message}");
        }
        finally
        {
            _swap.Present();
            image.Dispose();
        }
    }

    private static void SeedTriangleEntity(IEntityStore world)
    {
        uint ent = world.CreateEntity();
        world.Set(ent, new TriangleComponent
        {
            Positions = new float[]
            {
                 0.0f,  0.6f, 0.0f,
                -0.6f, -0.4f, 0.0f,
                 0.6f, -0.4f, 0.0f,
            },
            Colors = new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 },
        });
    }

    private void AllocateBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(
            new Avalonia.PixelSize((int)_width, (int)_height),
            new Avalonia.Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888,
            Avalonia.Platform.AlphaFormat.Opaque);
        Frame = _bitmap;
    }

    private void PushBytesToBitmap(byte[] bgra)
    {
        if (_bitmap is null) return;
        int needed = (int)(_width * _height * 4);
        int copy = Math.Min(bgra.Length, needed);
        using var lk = _bitmap.Lock();
        System.Runtime.InteropServices.Marshal.Copy(bgra, 0, lk.Address, copy);
    }

    public void RequestResize(uint w, uint h)
    {
        if (w == 0 || h == 0) return;
        if (_width == w && _height == h) return;
        _width = w; _height = h;
        AllocateBitmap();
        try
        {
            _swap?.Dispose();
            _swap = _device?.CreateSwapchain(_nsWindow, w, h);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[engine-viewport] resize failed: {ex.Message}");
        }
    }

    public void DisposeOnClose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _renderer?.Dispose();
        _swap?.Dispose();
        _device?.Dispose();
        _bitmap?.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose() => DisposeOnClose();
}
