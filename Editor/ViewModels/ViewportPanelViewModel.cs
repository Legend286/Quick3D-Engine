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
    private FileSystemWatcher? _scriptWatcher;
    private System.Threading.Timer? _debounceTimer;
    private const int DebounceMs = 500;
    private bool _autoHotReloadEnabled = true;
    private int _debounceGeneration;
    private readonly object _hotReloadLock = new();

    private Engine.Scene.SceneGraph _baseScene = new();
    public string CurrentSceneName { get; private set; } = "hello";
    
    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetProperty(ref _isDirty, value))
                OnDirtyChanged?.Invoke();
        }
    }
    public event Action? OnDirtyChanged;

    /// <summary>
    /// Enable/disable automatic hot-reload when Game/*.cs scripts change.
    /// Disabled during a reload to prevent re-entrancy.
    /// </summary>
    public bool AutoHotReloadEnabled
    {
        get => _autoHotReloadEnabled;
        set => _autoHotReloadEnabled = value;
    }

    public EcsWorld? World => _world;
    public event Action? OnWorldCreated;

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
            OnWorldCreated?.Invoke();
            // SeedTriangleEntity(_world);

            SetupSceneWatcher(_contentRoot);
            SetupScriptWatcher();

            // Perform an initial build and load to ensure any script edits made while
            // the editor was closed are picked up immediately on startup.
            HotReload();
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

    public void LoadScene(string sceneName)
    {
        CurrentSceneName = sceneName;
        try 
        {
            _baseScene = new Engine.Scene.SceneLoader(_contentRoot).Load(sceneName);
        }
        catch { _baseScene = new Engine.Scene.SceneGraph(); }
        try 
        {
            _gameLoop?.LoadScene(_contentRoot, sceneName);
        }
        catch (Exception ex)
        {
            Log.Error($"[Viewport] Failed to load scene '{sceneName}': {ex.Message}", "Editor");
        }
        IsDirty = false;
    }

    public void SaveScene()
    {
        if (_world == null) return;
        string path = Path.Combine(_contentRoot, "scenes", CurrentSceneName + ".scene.json");
        Engine.Scene.SceneSaver.Save(_world, _baseScene, path);
        IsDirty = false;
    }

    public void SaveSceneAs(string name)
    {
        CurrentSceneName = name;
        SaveScene();
    }

    public void NewScene()
    {
        _world?.Clear();
        _baseScene = new Engine.Scene.SceneGraph();
        CurrentSceneName = "New Scene";
        IsDirty = true;
    }

    public void MarkDirty()
    {
        IsDirty = true;
    }

    public void AddModelToScene(string mdlName)
    {
        if (_world == null || _device == null) return;
        
        var mdlPath = Path.Combine(_contentRoot, "assets", mdlName);
        if (!File.Exists(mdlPath)) return;
        
        var model = Engine.Assets.ModelLoader.LoadMdl(_device, mdlPath);
        ulong modelId = Engine.Assets.AssetRegistry.RegisterModel(model);
        
        ulong ent = _world.CreateEntity();
        _world.Set(ent, Engine.RHI.ModelComponent.Create(modelId));
        _world.Set(ent, Engine.Scene.Components.Transform.Default);
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
                    var (stdout, stderr) = RunDotnetBuild("build Game/Engine.Game.csproj -c Release", newProjectRoot);
                    Log.Info($"[Viewport] Build succeeded: {stdout.Trim()}", "Editor");
                    EmitBuildErrors(stderr);
                }
                catch (Exception ex)
                {
                    Log.Error($"[Viewport] Build failed: {ex.Message}", "Editor");
                    EmitBuildErrors(ex.ToString());
                }
            }

            // ---- UI-thread reload ----
            Dispatcher.UIThread.Post(() =>
            {
                _world = new EcsWorld();
                OnWorldCreated?.Invoke();
                // SeedTriangleEntity(_world);
                try
                {
                    LoadGameLoop();
                    SetupSceneWatcher(_contentRoot);
                    SetupScriptWatcher();
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
        // Prevent re-entrant auto-reload during an active reload cycle.
        _autoHotReloadEnabled = false;

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
                var (stdout, stderr) = RunDotnetBuild("build Game/Engine.Game.csproj -c Release", projectRoot);
                Log.Info($"[HotReload] Build succeeded: {stdout.Trim()}", "Editor");
                if (!string.IsNullOrWhiteSpace(stderr))
                    EmitBuildErrors(stderr);
                built = true;
            }
            catch (Exception ex)
            {
                Log.Error($"[HotReload] Build failed: {ex.Message}", "Editor");
                EmitBuildErrors(ex.ToString());
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
                    _autoHotReloadEnabled = true;
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
            var dt = (float)_stopwatch.Elapsed.TotalSeconds;
            _stopwatch.Restart();

            System.Collections.Generic.List<Engine.CBindings.NativeInput.EngineInputEvent>? frameEvents = null;
            lock (_events)
            {
                if (_events.Count > 0)
                {
                    frameEvents = new(_events);
                    _events.Clear();
                }
            }

            var input = new Engine.RHI.InputState
            {
                DeltaTime = dt,
                LogicalWidth = _host != null ? (float)_host.Bounds.Width : _swap.Width,
                LogicalHeight = _host != null ? (float)_host.Bounds.Height : _swap.Height,
                RenderScale = (float)ComputeRenderScaling(),
                MouseX = _ptrX,
                MouseY = _ptrY,
                MouseDeltaX = _ptrDx,
                MouseDeltaY = _ptrDy,
                MouseDownLeft = _leftDown,
                MouseDownRight = _rightDown,
                MouseDownMiddle = _middleDown,
                KeyW = _keyW,
                KeyA = _keyA,
                KeyS = _keyS,
                KeyD = _keyD,
                Events = frameEvents
            };

            _gameLoop.Update(input);
            _ptrDx = 0;
            _ptrDy = 0;

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

    private readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();
    private float _ptrDx;
    private float _ptrDy;
    private float _ptrX;
    private float _ptrY;
    private bool _leftDown;
    private bool _rightDown;
    private bool _middleDown;
    private bool _keyW, _keyA, _keyS, _keyD;

    public void AddPointerDelta(float dx, float dy)
    {
        _ptrDx += dx;
        _ptrDy += dy;
    }

    public void UpdatePointerState(float x, float y, bool left, bool right, bool middle)
    {
        _ptrX = x;
        _ptrY = y;
        _leftDown = left;
        _rightDown = right;
        _middleDown = middle;

        QueueEvent(new Engine.CBindings.NativeInput.EngineInputEvent
        {
            Type = 2, // MouseMove
            MouseX = x,
            MouseY = y
        });
    }

    private System.Collections.Generic.List<Engine.CBindings.NativeInput.EngineInputEvent> _events = new();

    public void QueueEvent(Engine.CBindings.NativeInput.EngineInputEvent ev)
    {
        lock (_events)
        {
            _events.Add(ev);
        }
    }

    public void QueueCharEvent(char c)
    {
        QueueEvent(new Engine.CBindings.NativeInput.EngineInputEvent
        {
            Type = 6, // Char
            CharCode = c
        });
    }
    
    public void QueueMouseButtonEvent(int button, bool isDown)
    {
        QueueEvent(new Engine.CBindings.NativeInput.EngineInputEvent
        {
            Type = isDown ? 3u : 4u, // MouseDown / MouseUp
            MouseButton = (Engine.CBindings.NativeInput.EngineMouseButton)button
        });
    }
    
    public void QueueScrollEvent(float scrollX, float scrollY)
    {
        QueueEvent(new Engine.CBindings.NativeInput.EngineInputEvent
        {
            Type = 5, // Scroll
            ScrollX = scrollX,
            ScrollY = scrollY
        });
    }

    public void SetKeyState(Avalonia.Input.Key key, bool isDown)
    {
        switch (key)
        {
            case Avalonia.Input.Key.W: _keyW = isDown; break;
            case Avalonia.Input.Key.A: _keyA = isDown; break;
            case Avalonia.Input.Key.S: _keyS = isDown; break;
            case Avalonia.Input.Key.D: _keyD = isDown; break;
        }

        var ek = MapAvaloniaKey(key);
        if (ek != Engine.CBindings.NativeInput.EngineKey.Unknown)
        {
            QueueEvent(new Engine.CBindings.NativeInput.EngineInputEvent
            {
                Type = isDown ? 0u : 1u,
                Key = ek
            });
        }
    }

    private Engine.CBindings.NativeInput.EngineKey MapAvaloniaKey(Avalonia.Input.Key key)
    {
        return key switch
        {
            Avalonia.Input.Key.A => Engine.CBindings.NativeInput.EngineKey.A,
            Avalonia.Input.Key.B => Engine.CBindings.NativeInput.EngineKey.B,
            Avalonia.Input.Key.C => Engine.CBindings.NativeInput.EngineKey.C,
            Avalonia.Input.Key.D => Engine.CBindings.NativeInput.EngineKey.D,
            Avalonia.Input.Key.E => Engine.CBindings.NativeInput.EngineKey.E,
            Avalonia.Input.Key.F => Engine.CBindings.NativeInput.EngineKey.F,
            Avalonia.Input.Key.G => Engine.CBindings.NativeInput.EngineKey.G,
            Avalonia.Input.Key.H => Engine.CBindings.NativeInput.EngineKey.H,
            Avalonia.Input.Key.I => Engine.CBindings.NativeInput.EngineKey.I,
            Avalonia.Input.Key.J => Engine.CBindings.NativeInput.EngineKey.J,
            Avalonia.Input.Key.K => Engine.CBindings.NativeInput.EngineKey.K,
            Avalonia.Input.Key.L => Engine.CBindings.NativeInput.EngineKey.L,
            Avalonia.Input.Key.M => Engine.CBindings.NativeInput.EngineKey.M,
            Avalonia.Input.Key.N => Engine.CBindings.NativeInput.EngineKey.N,
            Avalonia.Input.Key.O => Engine.CBindings.NativeInput.EngineKey.O,
            Avalonia.Input.Key.P => Engine.CBindings.NativeInput.EngineKey.P,
            Avalonia.Input.Key.Q => Engine.CBindings.NativeInput.EngineKey.Q,
            Avalonia.Input.Key.R => Engine.CBindings.NativeInput.EngineKey.R,
            Avalonia.Input.Key.S => Engine.CBindings.NativeInput.EngineKey.S,
            Avalonia.Input.Key.T => Engine.CBindings.NativeInput.EngineKey.T,
            Avalonia.Input.Key.U => Engine.CBindings.NativeInput.EngineKey.U,
            Avalonia.Input.Key.V => Engine.CBindings.NativeInput.EngineKey.V,
            Avalonia.Input.Key.W => Engine.CBindings.NativeInput.EngineKey.W,
            Avalonia.Input.Key.X => Engine.CBindings.NativeInput.EngineKey.X,
            Avalonia.Input.Key.Y => Engine.CBindings.NativeInput.EngineKey.Y,
            Avalonia.Input.Key.Z => Engine.CBindings.NativeInput.EngineKey.Z,
            Avalonia.Input.Key.D0 => Engine.CBindings.NativeInput.EngineKey.Num0,
            Avalonia.Input.Key.D1 => Engine.CBindings.NativeInput.EngineKey.Num1,
            Avalonia.Input.Key.D2 => Engine.CBindings.NativeInput.EngineKey.Num2,
            Avalonia.Input.Key.D3 => Engine.CBindings.NativeInput.EngineKey.Num3,
            Avalonia.Input.Key.D4 => Engine.CBindings.NativeInput.EngineKey.Num4,
            Avalonia.Input.Key.D5 => Engine.CBindings.NativeInput.EngineKey.Num5,
            Avalonia.Input.Key.D6 => Engine.CBindings.NativeInput.EngineKey.Num6,
            Avalonia.Input.Key.D7 => Engine.CBindings.NativeInput.EngineKey.Num7,
            Avalonia.Input.Key.D8 => Engine.CBindings.NativeInput.EngineKey.Num8,
            Avalonia.Input.Key.D9 => Engine.CBindings.NativeInput.EngineKey.Num9,
            Avalonia.Input.Key.Escape => Engine.CBindings.NativeInput.EngineKey.Escape,
            Avalonia.Input.Key.Enter => Engine.CBindings.NativeInput.EngineKey.Enter,
            Avalonia.Input.Key.Tab => Engine.CBindings.NativeInput.EngineKey.Tab,
            Avalonia.Input.Key.Back => Engine.CBindings.NativeInput.EngineKey.Backspace,
            Avalonia.Input.Key.Insert => Engine.CBindings.NativeInput.EngineKey.Insert,
            Avalonia.Input.Key.Delete => Engine.CBindings.NativeInput.EngineKey.Delete,
            Avalonia.Input.Key.Right => Engine.CBindings.NativeInput.EngineKey.Right,
            Avalonia.Input.Key.Left => Engine.CBindings.NativeInput.EngineKey.Left,
            Avalonia.Input.Key.Down => Engine.CBindings.NativeInput.EngineKey.Down,
            Avalonia.Input.Key.Up => Engine.CBindings.NativeInput.EngineKey.Up,
            Avalonia.Input.Key.PageUp => Engine.CBindings.NativeInput.EngineKey.PageUp,
            Avalonia.Input.Key.PageDown => Engine.CBindings.NativeInput.EngineKey.PageDown,
            Avalonia.Input.Key.Home => Engine.CBindings.NativeInput.EngineKey.Home,
            Avalonia.Input.Key.End => Engine.CBindings.NativeInput.EngineKey.End,
            Avalonia.Input.Key.LeftShift => Engine.CBindings.NativeInput.EngineKey.LeftShift,
            Avalonia.Input.Key.RightShift => Engine.CBindings.NativeInput.EngineKey.RightShift,
            Avalonia.Input.Key.LeftCtrl => Engine.CBindings.NativeInput.EngineKey.LeftCtrl,
            Avalonia.Input.Key.RightCtrl => Engine.CBindings.NativeInput.EngineKey.RightCtrl,
            Avalonia.Input.Key.LeftAlt => Engine.CBindings.NativeInput.EngineKey.LeftAlt,
            Avalonia.Input.Key.RightAlt => Engine.CBindings.NativeInput.EngineKey.RightAlt,
            _ => Engine.CBindings.NativeInput.EngineKey.Unknown
        };
    }

    /* private static void SeedTriangleEntity(IEntityStore world)
    {
        ulong ent = world.CreateEntity();
        Log.Debug($"[Viewport] Seeding default mesh entity {ent}", "Editor");
        // Needs proper mesh ID now
    } */

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

    /// <summary>
    /// Watch Game/*.cs files for changes and trigger hot-reload after a
    /// debounce period. Prevents rapid-fire rebuilds when saving multiple
    /// files in quick succession.
    /// </summary>
    private void SetupScriptWatcher()
    {
        _scriptWatcher?.Dispose();
        _debounceTimer?.Dispose();

        string projectRoot = App.ProjectRoot;
        if (string.IsNullOrEmpty(projectRoot)) return;

        string gameDir = Path.Combine(projectRoot, "Game");
        if (!Directory.Exists(gameDir)) return;

        Log.Info($"[Watcher] Watching scripts in: {gameDir}", "Editor");
        _scriptWatcher = new FileSystemWatcher(gameDir, "*.cs")
        {
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = true,
        };

        _scriptWatcher.Changed += OnScriptChanged;
        _scriptWatcher.Created += OnScriptChanged;
        _scriptWatcher.Renamed += OnScriptChanged;
    }

    private void OnScriptChanged(object sender, FileSystemEventArgs e)
    {
        if (!_autoHotReloadEnabled) return;

        lock (_hotReloadLock)
        {
            int gen = Interlocked.Increment(ref _debounceGeneration);
            Log.Debug($"[Watcher] Script change detected: {Path.GetFileName(e.FullPath)} (debounce {DebounceMs}ms, gen={gen})", "Editor");
            _debounceTimer?.Dispose();
            _debounceTimer = new System.Threading.Timer(_ =>
            {
                if (Interlocked.CompareExchange(ref _debounceGeneration, gen, gen) != gen)
                {
                    Log.Debug("[HotReload] Stale debounce callback skipped", "Editor");
                    return;
                }
                Dispatcher.UIThread.Post(() =>
                {
                    Log.Info("[HotReload] Auto-reload triggered by script change", "Editor");
                    HotReload();
                });
            }, null, DebounceMs, System.Threading.Timeout.Infinite);
        }
    }

    /// <summary>
    /// Forward build errors into the engine log with source-file context so
    /// the console panel can render them as clickable file:line links.
    /// </summary>
    private static void EmitBuildErrors(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return;

        var lines = stderr.Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            // Categorize: error CS####, warning CS####, or generic
            if (trimmed.Contains("error CS") || trimmed.Contains("error :"))
                Log.Error($"[Build] {trimmed}", "Build");
            else if (trimmed.Contains("warning CS") || trimmed.Contains("warning :"))
                Log.Warn($"[Build] {trimmed}", "Build");
            else
                Log.Info($"[Build] {trimmed}", "Build");
        }
    }

    public void DisposeOnClose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _sceneWatcher?.Dispose();
        _sceneWatcher = null;
        _scriptWatcher?.Dispose();
        _scriptWatcher = null;
        _debounceTimer?.Dispose();
        _debounceTimer = null;
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
