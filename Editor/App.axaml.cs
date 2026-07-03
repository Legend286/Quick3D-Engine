// SPDX-License-Identifier: MIT
using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Engine.CBindings;

namespace Engine.Editor;

public partial class App : Application
{
    public static string ProjectRoot { get; internal set; } = "";
    public static string EngineSourceRoot { get; internal set; } = "";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var projectRoot = EngineLogBootstrap.ResolveProjectRoot(desktop.Args ?? Array.Empty<string>());
            
            if (string.IsNullOrEmpty(projectRoot))
            {
                // No project found or specified. Show Welcome Window.
                App.EngineSourceRoot = EngineLogBootstrap.ResolveEngineSourceRoot();
                desktop.MainWindow = new Views.WelcomeWindow();
            }
            else
            {
                // Project found, init engine and launch MainWindow
                EngineLogBootstrap.InitFromProject(projectRoot);
                desktop.MainWindow = new MainWindow();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>
/// Initialises engine_log from the .eeproj/modules.json#logging block. Falls
/// back to INFO on missing config per docs/console/logging-config.md.
/// </summary>
internal static class EngineLogBootstrap
{
    public static void InitFromProject(string projectRoot)
    {
        App.ProjectRoot = projectRoot;
        App.EngineSourceRoot = ResolveEngineSourceRoot();
        Console.WriteLine($"[AppBootstrap] Resolved ProjectRoot: '{projectRoot}'");
        Console.WriteLine($"[AppBootstrap] Resolved EngineSourceRoot: '{App.EngineSourceRoot}'");
        Console.WriteLine($"[AppBootstrap] AppDomain BaseDirectory: '{AppDomain.CurrentDomain.BaseDirectory}'");

        try
        {
            Directory.SetCurrentDirectory(projectRoot);
            Console.WriteLine($"[AppBootstrap] Set CurrentDirectory to: '{projectRoot}'");
            Directory.CreateDirectory(Path.Combine(projectRoot, "out", "logs"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AppBootstrap] Failed to set directory or create logs: {ex.Message}");
        }

        var logging = ModulesJsonLoggingReader.Read(projectRoot);

        var config = EngineLogConfigBuilder.Build(
            globalLevel: logging.GlobalLevel,
            ringCapacityRecords: logging.RingCapacity,
            maxMsgBytes: logging.MaxMsgBytes,
            enableCrashDump: logging.EnableCrashDump,
            crashDumpPath: logging.CrashDumpPath);

        int rc = EngineLog.EngineLogInit(in config);
        if (rc != 0)
        {
            Console.Error.WriteLine($"[engine_log] init failed rc={rc}");
            return;
        }

        foreach (var (module, level) in logging.ModuleOverrides)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(module);
            unsafe
            {
                fixed (byte* p = bytes)
                {
                    EngineLog.EngineLogSetModuleLevel((IntPtr)p, level);
                }
            }
        }
    }

    public static string? ResolveProjectRoot(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; ++i)
        {
            if (string.Equals(args[i], "--project", StringComparison.Ordinal))
                return Path.GetFullPath(args[i + 1]);
        }

        // Try walking up from AppDomain.CurrentDomain.BaseDirectory first
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            var dir = baseDir;
            for (int i = 0; i < 8; ++i)
            {
                if (Directory.Exists(Path.Combine(dir, ".eeproj")))
                    return dir;
                var parent = Directory.GetParent(dir);
                if (parent is null) break;
                dir = parent.FullName;
            }
        }

        // Fallback to walking up from CurrentDirectory
        var currDir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 6; ++i)
        {
            if (Directory.Exists(Path.Combine(currDir, ".eeproj")))
                return currDir;
            var parent = Directory.GetParent(currDir);
            if (parent is null) break;
            currDir = parent.FullName;
        }

        return null;
    }

    public static string ResolveEngineSourceRoot()
    {
        // Try to find the directory containing 'engine_cs' or 'CMakeLists.txt' representing the engine repo
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            var dir = baseDir;
            for (int i = 0; i < 8; ++i)
            {
                if (Directory.Exists(Path.Combine(dir, "engine_cs")) && Directory.Exists(Path.Combine(dir, "Content")))
                    return dir;
                var parent = Directory.GetParent(dir);
                if (parent is null) break;
                dir = parent.FullName;
            }
        }
        
        var currDir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 6; ++i)
        {
            if (Directory.Exists(Path.Combine(currDir, "engine_cs")) && Directory.Exists(Path.Combine(currDir, "Content")))
                return currDir;
            var parent = Directory.GetParent(currDir);
            if (parent is null) break;
            currDir = parent.FullName;
        }

        return Directory.GetCurrentDirectory(); // Fallback
    }
}
