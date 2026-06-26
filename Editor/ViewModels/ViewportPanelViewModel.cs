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
using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Engine.CBindings;
using Engine.Editor.Views;
using Engine.RHI;

namespace Engine.Editor.ViewModels;

public sealed class ViewportPanelViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer;
    private RhiDevice? _device;
    private RhiSwapchain? _swap;
    private IGameLoop? _gameLoop;
    private GameAssemblyLoadContext? _loadContext;
    private EcsWorld? _world;
    private IntPtr _nsView;
    private ViewportMetalLayerHost? _host;
    private uint _width = ViewportMetalLayerHost.DefaultInitialWidth;
    private uint _height = ViewportMetalLayerHost.DefaultInitialHeight;
    private bool _attached;
    private bool _disposed;
    private string _contentRoot;

    public ViewportPanelViewModel(string contentRoot, string sceneName = "hello")
    {
        _contentRoot = contentRoot;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
    }

    public void UpdateContentRoot(string contentRoot)
    {
        _contentRoot = contentRoot;
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
            _world = new EcsWorld();
            SeedTriangleEntity(_world);
            
            LoadGameLoop();

            _timer.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[engine-viewport] init failed: {ex.Message}");
        }
    }

    private string ResolveGameDllPath()
    {
        if (!string.IsNullOrEmpty(App.ProjectRoot))
        {
            var searchPaths = new[]
            {
                Path.Combine(App.ProjectRoot, "Game", "bin", "Release", "net8.0", "osx-arm64", "Engine.Game.dll"),
                Path.Combine(App.ProjectRoot, "Game", "bin", "Debug", "net8.0", "osx-arm64", "Engine.Game.dll"),
                Path.Combine(App.ProjectRoot, "Game", "bin", "Release", "net8.0", "Engine.Game.dll"),
                Path.Combine(App.ProjectRoot, "Game", "bin", "Debug", "net8.0", "Engine.Game.dll"),
            };
            foreach (var path in searchPaths)
            {
                if (File.Exists(path)) return path;
            }
        }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Engine.Game.dll");
    }

    public void LoadGameLoop()
    {
        if (_device is null || _swap is null || _world is null) return;

        string dllPath = ResolveGameDllPath();
        if (!File.Exists(dllPath))
        {
            Console.Error.WriteLine($"[HotReload] Game DLL not found at: {dllPath}");
            return;
        }

        Console.WriteLine($"[HotReload] Loading Game assembly from: {dllPath}");
        _loadContext = new GameAssemblyLoadContext(dllPath);
        var assembly = _loadContext.LoadFromAssemblyName(new AssemblyName("Engine.Game"));

        Type? loopType = null;
        foreach (var type in assembly.GetTypes())
        {
            if (typeof(IGameLoop).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract)
            {
                loopType = type;
                break;
            }
        }

        if (loopType is null)
        {
            throw new InvalidOperationException("Could not find a class implementing IGameLoop in the loaded assembly.");
        }

        _gameLoop = (IGameLoop)Activator.CreateInstance(loopType)!;
        _gameLoop.Init(_device.Handle, _swap.Handle, _world);
        _gameLoop.LoadScene(_contentRoot, "hello");
    }

    public void ReloadProject(string newProjectRoot)
    {
        Console.WriteLine($"[Viewport] Switching project root to: {newProjectRoot}");
        _timer.Stop();

        _gameLoop?.Dispose();
        _gameLoop = null;

        if (_loadContext is not null)
        {
            _loadContext.Unload();
            _loadContext = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        _world?.Dispose();
        _world = null;

        App.ProjectRoot = newProjectRoot;
        try
        {
            Directory.SetCurrentDirectory(newProjectRoot);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Viewport] Failed to set directory: {ex.Message}");
        }

        UpdateContentRoot(Path.Combine(newProjectRoot, "Content"));

        _world = new EcsWorld();
        SeedTriangleEntity(_world);

        // Ensure the Game DLL exists; if not, build it.
        string dllPath = ResolveGameDllPath();
        if (!File.Exists(dllPath))
        {
            try
            {
                Console.WriteLine("[Viewport] Game DLL not found in new project root. Rebuilding...");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "dotnet",
                    Arguments = "build Game/Engine.Game.csproj -c Release",
                    WorkingDirectory = App.ProjectRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                proc?.WaitForExit();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Viewport] Initial build failed: {ex.Message}");
            }
        }

        try
        {
            LoadGameLoop();
            Console.WriteLine("[Viewport] Loaded game loop for new project root.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Viewport] Loading failed: {ex.Message}");
        }
        finally
        {
            _timer.Start();
        }
    }

    public void HotReload()
    {
        Console.WriteLine("[HotReload] Initiating hot-reload...");
        _timer.Stop();

        _gameLoop?.Dispose();
        _gameLoop = null;

        if (_loadContext is not null)
        {
            _loadContext.Unload();
            _loadContext = null;

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        try
        {
            Console.WriteLine("[HotReload] Rebuilding Game project...");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "build Game/Engine.Game.csproj -c Release",
                WorkingDirectory = App.ProjectRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
            {
                throw new InvalidOperationException("Failed to start dotnet build process.");
            }
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                string err = proc.StandardError.ReadToEnd();
                string stdout = proc.StandardOutput.ReadToEnd();
                Console.Error.WriteLine($"[HotReload] Build failed (exit code {proc.ExitCode}):\n{err}\n{stdout}");
                return;
            }
            Console.WriteLine("[HotReload] Build succeeded.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HotReload] Build exception: {ex.Message}");
            return;
        }

        try
        {
            LoadGameLoop();
            Console.WriteLine("[HotReload] Hot-reload complete!");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[HotReload] Loading failed: {ex.Message}");
        }
        finally
        {
            _timer.Start();
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
        if (_device is null || _swap is null || _gameLoop is null) return;
        if (!_swap.TryAcquireNextImage(out RhiTexture? image) || image is null)
        {
            return;
        }
        try
        {
            _gameLoop.RenderFrame(image, _swap.Width, _swap.Height);
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
        ulong ent = world.CreateEntity();
        world.Set(ent, TriangleComponent.Create(
            new float[]
            {
                 0.0f,  0.6f, 0.0f,
                -0.6f, -0.4f, 0.0f,
                 0.6f, -0.4f, 0.0f,
            },
            new float[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 }
        ));
    }

    public void DisposeOnClose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _gameLoop?.Dispose();
        _gameLoop = null;
        if (_loadContext is not null)
        {
            _loadContext.Unload();
            _loadContext = null;
        }
        _world?.Dispose();
        _world = null;
        _swap?.Dispose();
        _swap = null;
        _device?.Dispose();
        _device = null;
        GC.SuppressFinalize(this);
    }

    public void Dispose() => DisposeOnClose();
}
