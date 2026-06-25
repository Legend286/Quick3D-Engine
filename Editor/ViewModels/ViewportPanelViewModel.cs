// SPDX-License-Identifier: MIT
// ViewportPanelViewModel: owns the Metal swapchain on the Avalonia-hosted
// NSView (via ViewportMetalLayerHost), drives a 60 fps render loop, and
// lets each frame's Metal-rendered drawable composite directly into the
// view via CAMetalLayer sublayer integration.
//
// Architecture note: there is no WriteableBitmap + Image path anymore. The
// Metal layer IS the surface the user sees. Render-graph passes encode
// against the swapchain image acquired from rhi_acquire_next_image; on
// submit, CoreAnimation composites the drawable into the NSView's layer
// tree; Avalonia sees it as a child visual. This eliminates the readback-
// and-Skia-reupload path that the previous architecture carried and the
// sources of "blank viewport" bugs it surfaced (Avalonia's render timer
// clobbering the contentView layer we had reassigned).
//
// Resize handling: subscribe to the host control's SizeChanged; rebuild
// the swapchain at the new Bounds so CAMetalLayer.drawableSize matches
// the visible region and the next-drawable cadence tracks the layout.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Engine.CBindings;
using Engine.Editor.Views;
using Engine.Game;
using Engine.RHI;
using Engine.RenderGraph;

namespace Engine.Editor.ViewModels;

public sealed class ViewportPanelViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer;
    private RhiDevice? _device;
    private RhiSwapchain? _swap;
    private Renderer? _renderer;
    private IntPtr _nsView;
    private ViewportMetalLayerHost? _host;
    private uint _width = ViewportMetalLayerHost.DefaultInitialWidth;
    private uint _height = ViewportMetalLayerHost.DefaultInitialHeight;
    private bool _attached;
    private bool _disposed;
    private readonly string _contentRoot;

    public ViewportPanelViewModel(string contentRoot, string sceneName = "hello")
    {
        _contentRoot = contentRoot;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
    }

    /// <summary>Called from <c>MainWindow.Opened</c> AFTER Avalonia has laid
    /// out the visual subtree and the ViewportMetalLayerHost inside it. The
    /// host has already received its <c>CreateNativeControlCore</c> callback,
    /// so its NSView* is live.</summary>
    public void AttachToVisualTree(Window host)
    {
        if (_attached) return;
        _attached = true;
        try
        {
            _host = LocateHost(host);
            if (_host is null || _host.NativeViewHandle == IntPtr.Zero)
            {
                Console.Error.WriteLine("[engine-viewport] host NSView handle is zero.");
                return;
            }
            _nsView = _host.NativeViewHandle;
            SyncDimensionsFromHost();
            _device = new RhiDevice();
            _swap = _device.CreateSwapchain(_nsView, _width, _height);
            var world = new EcsWorld();
            SeedTriangleEntity(world);
            _renderer = new Renderer(_device, _swap, world);
            _renderer.LoadScene(_contentRoot, "hello");

            _timer.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[engine-viewport] init failed: {ex.Message}");
        }
    }

    private ViewportMetalLayerHost? LocateHost(Visual root)
    {
        foreach (var v in root.GetVisualDescendants())
            if (v is ViewportMetalLayerHost h) return h;
        return null;
    }

    private void SyncDimensionsFromHost()
    {
        if (_host is null) return;
        double scale = ComputeRenderScaling();
        double w = _host.Bounds.Width;
        double h = _host.Bounds.Height;
        if (w < 1 || h < 1) return;
        _width = (uint)Math.Max(1, w * scale);
        _height = (uint)Math.Max(1, h * scale);
    }

    private double ComputeRenderScaling()
    {
        if (_host is null) return 1.0;
        var tl = TopLevel.GetTopLevel(_host);
        if (tl is not null && tl.RenderScaling > 0) return tl.RenderScaling;
        return 1.0;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_device is null || _swap is null || _renderer is null) return;
        if (!_swap.TryAcquireNextImage(out RhiTexture? image) || image is null)
            return;
        try
        {
            _renderer.RenderFrame(image, _swap.Width, _swap.Height);
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

    public void DisposeOnClose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _renderer?.Dispose();
        _swap?.Dispose();
        _device?.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose() => DisposeOnClose();
}
