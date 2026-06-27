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
    private FileSystemWatcher? _sceneWatcher;

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
                Log.Error("[engine-viewport] host NSView handle is zero.", "Editor");
                return;
            }
            _nsView = _host.NativeViewHandle;
            SyncDimensionsFromHost();
            _device = new RhiDevice();
            _swap = _device.CreateSwapchain(_nsView, _width, _height);
            _world = new EcsWorld();
            SeedTriangleEntity(_world);

            LoadGameLoop();
            SetupSceneWatcher(_contentRoot);

            _timer.Start();
        }
        catch (Exception ex)
        {
            Log.Error($"[engine-viewport] init failed: {ex.Message}", "Editor");
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
            // Return a default path inside the project so File.Exists is false and triggers build
            return Path.Combine(App.ProjectRoot, "Game", "bin", "Release", "net8.0", "Engine.Game.dll");
        }
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Engine.Game.dll");
    }

    private string ResolveStartupScene()
    {
        try
        {
            string scenesJson = Path.Combine(App.ProjectRoot, ".eeproj", "scenes.json");
            if (File.Exists(scenesJson))
            {
                string text = File.ReadAllText(scenesJson);
                var match = System.Text.RegularExpressions.Regex.Match(
                    text, "\"startup_scene\"\\s*:\\s*\"([^\"]+)\"");
                if (match.Success)
                {
                    string scenePath = match.Groups[1].Value;
                    string name = Path.GetFileNameWithoutExtension(
                        Path.GetFileNameWithoutExtension(scenePath));
                    return name;
                }
            }
        }
        catch { }
        return "hello";
    }

    private static string ResolveDotnetExe()
    {
        // App bundles launched from Finder/Dock don't inherit the shell PATH.
        // Probe the known macOS install locations in priority order.
        string[] candidates =
        {
            "/usr/local/share/dotnet/dotnet",
            "/usr/local/bin/dotnet",
            "/opt/homebrew/bin/dotnet",
            "/opt/homebrew/share/dotnet/dotnet",
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return "dotnet"; // last-resort: maybe the PATH is set
    }

    private static (string stdout, string stderr) RunDotnetBuild(string args, string workDir)
    {
        string dotnetExe = ResolveDotnetExe();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = dotnetExe,
            Arguments = args,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        // Inject the dotnet directory into PATH so MSBuild child processes
        // can also locate the dotnet host without inheriting the shell PATH.
        string dotnetDir = Path.GetDirectoryName(dotnetExe) ?? "";
        if (!string.IsNullOrEmpty(dotnetDir))
        {
            string existingPath = psi.Environment.TryGetValue("PATH", out var p) ? p ?? "" : "";
            psi.Environment["PATH"] = string.IsNullOrEmpty(existingPath)
                ? dotnetDir
                : $"{dotnetDir}:{existingPath}";
        }

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {dotnetExe}");
        // Read both streams asynchronously to prevent pipe-buffer deadlock.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            throw new Exception($"dotnet {args} exited {proc.ExitCode}\n{stderr}\n{stdout}");
        return (stdout, stderr);
    }

    public void LoadGameLoop()
    {
        if (_device is null || _swap is null || _world is null) return;

        string dllPath = ResolveGameDllPath();
        if (!File.Exists(dllPath))
        {
            Log.Error($"[HotReload] Game DLL not found at: {dllPath}", "Editor");
            return;
        }

        Log.Info($"[HotReload] Loading Game assembly from: {dllPath}", "Editor");
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
            throw new InvalidOperationException("No IGameLoop implementation found in Engine.Game.");

        _gameLoop = (IGameLoop)Activator.CreateInstance(loopType)!;
        _gameLoop.Init(_device.Handle, _swap.Handle, _world);
        _gameLoop.LoadScene(_contentRoot, ResolveStartupScene());
    }

    /// <summary>
    /// Initiates a project reload. The method first tears down the current
    /// game loop on the calling (UI) thread, then dispatches the blocking
    /// dotnet-build step to a thread-pool thread, and finally marshals the
    /// game loop re-initialisation back to the UI thread via
    /// Dispatcher.UIThread.Post so Metal objects are never touched from a
    /// worker thread.
    /// </summary>
    public void BeginReloadProject(string newProjectRoot)
    {
        // ---- UI-thread teardown ----
        Log.Info($"[Viewport] Switching project root to: {newProjectRoot}", "Editor");
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
        try { Directory.SetCurrentDirectory(newProjectRoot); }
        catch (Exception ex) { Log.Error($"[Viewport] chdir failed: {ex.Message}", "Editor"); }

        UpdateContentRoot(Path.Combine(newProjectRoot, "Content"));

        // ---- background build (doesn't touch any RHI/UI objects) ----
        System.Threading.Tasks.Task.Run(() =>
        {
            string dllPath = ResolveGameDllPath();
            if (!File.Exists(dllPath))
            {
                try
                {
                    Log.Info("[Viewport] Game DLL not found. Building...", "Editor");
                    var (stdout, _) = RunDotnetBuild("build Game/Engine.Game.csproj -c Release", newProjectRoot);
                    Log.Info($"[Viewport] Build succeeded: {stdout.Trim()}", "Editor");
                }
                catch (Exception ex)
                {
                    Log.Error($"[Viewport] Build failed: {ex.Message}", "Editor");
                }
            }

            // ---- UI-thread reload ----
            Dispatcher.UIThread.Post(() =>
            {
                _world = new EcsWorld();
                SeedTriangleEntity(_world);
                try
                {
                    LoadGameLoop();
                    SetupSceneWatcher(_contentRoot);
                    Log.Info($"[Viewport] Project loaded: {newProjectRoot}", "Editor");
                }
                catch (Exception ex)
                {
                    Log.Error($"[Viewport] LoadGameLoop failed: {ex.Message}", "Editor");
                }
                finally
                {
                    _timer.Start();
                }
            });
        });
    }

    public void HotReload()
    {
        // ---- UI-thread teardown ----
        Log.Info("[HotReload] Initiating hot-reload...", "Editor");
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

        // ---- background build ----
        string projectRoot = App.ProjectRoot;
        System.Threading.Tasks.Task.Run(() =>
        {
            bool built = false;
            try
            {
                Log.Info("[HotReload] Rebuilding Game project...", "Editor");
                var (stdout, _) = RunDotnetBuild("build Game/Engine.Game.csproj -c Release", projectRoot);
                Log.Info($"[HotReload] Build succeeded: {stdout.Trim()}", "Editor");
                built = true;
            }
            catch (Exception ex)
            {
                Log.Error($"[HotReload] Build failed: {ex.Message}", "Editor");
            }

            // ---- UI-thread reload ----
            Dispatcher.UIThread.Post(() =>
            {
                if (!built)
                {
                    _timer.Start();
                    return;
                }
                try
                {
                    LoadGameLoop();
                    Log.Info("[HotReload] Hot-reload complete!", "Editor");
                }
                catch (Exception ex)
                {
                    Log.Error($"[HotReload] Loading failed: {ex.Message}", "Editor");
                }
                finally
                {
                    _timer.Start();
                }
            });
        });
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
            Log.Error($"[engine-viewport] frame failed: {ex.Message}", "Editor");
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
            new float[] {  0.0f,  0.6f, 0.0f,
                          -0.6f, -0.4f, 0.0f,
                           0.6f, -0.4f, 0.0f },
            new float[] { 1, 0, 0,  0, 1, 0,  0, 0, 1 }
        ));
    }

    private void SetupSceneWatcher(string contentRoot)
    {
        _sceneWatcher?.Dispose();
        _sceneWatcher = null;

        string scenesDir = Path.Combine(contentRoot, "scenes");
        if (Directory.Exists(scenesDir))
        {
            _sceneWatcher = new FileSystemWatcher(scenesDir, "*.scene.json")
            {
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
            };
            _sceneWatcher.Changed += (s, e) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    Log.Info($"[Watcher] Scene file changed: {e.FullPath}. Reloading...", "Editor");
                    _gameLoop?.LoadScene(_contentRoot, ResolveStartupScene());
                });
            };
        }
    }

    public void DisposeOnClose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _sceneWatcher?.Dispose();
        _sceneWatcher = null;
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
