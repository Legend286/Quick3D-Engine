// SPDX-License-Identifier: MIT

using System;
using Avalonia;

namespace Engine.Editor;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Fix for macOS Finder launch where CWD is /
        if (AppDomain.CurrentDomain.BaseDirectory.Contains(".app/Contents/MacOS"))
        {
            var appRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "../../../../"));
            Environment.CurrentDirectory = appRoot;
        }

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Engine.CBindings.Log.Error($"[AppDomain] Unhandled exception: {ex}", "Editor");
            }
        };

        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, assemblyName) =>
        {
            if (assemblyName.Name != null && assemblyName.Name.StartsWith("StbImageSharp"))
            {
                string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, assemblyName.Name + ".dll");
                if (System.IO.File.Exists(path))
                {
                    return context.LoadFromAssemblyPath(path);
                }
            }
            return null;
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Engine.CBindings.Log.Error($"[TaskScheduler] Unobserved exception: {e.Exception}", "Editor");
            e.SetObserved();
        };

        try
        {
            var result = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            Engine.CBindings.EngineLog.EngineLogShutdown();
            return result;
        }
        catch (Exception ex)
        {
            File.WriteAllText("/tmp/editor_crash.txt", ex.ToString());
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UseSkia()
                     .UsePlatformDetect()
                     .LogToTrace();
}
