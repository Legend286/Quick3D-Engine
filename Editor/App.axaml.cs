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
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            EngineLogBootstrap.InitFromProject(desktop.Args ?? Array.Empty<string>());
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>
/// Initialises engine_log from the .eeproj/modules.json#logging block. Falls
/// back to INFO on missing config per docs/console/logging-config.md.
internal static class EngineLogBootstrap
{
    public static void InitFromProject(string[] args)
    {
        var projectRoot = ResolveProjectRoot(args);
        var logging = ModulesJsonLoggingReader.Read(projectRoot);

        var config = EngineLogConfigBuilder.Build(
            globalLevel:          logging.GlobalLevel,
            ringCapacityRecords:  logging.RingCapacity,
            maxMsgBytes:          logging.MaxMsgBytes,
            enableCrashDump:      logging.EnableCrashDump,
            crashDumpPath:        logging.CrashDumpPath);

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

    private static string ResolveProjectRoot(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; ++i)
        {
            if (string.Equals(args[i], "--project", StringComparison.Ordinal))
                return Path.GetFullPath(args[i + 1]);
        }

        // Editor default cwd walk: look for .eeproj
        var dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 6; ++i)
        {
            if (Directory.Exists(Path.Combine(dir, ".eeproj")))
                return dir;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
