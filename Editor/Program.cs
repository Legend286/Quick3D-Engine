// SPDX-License-Identifier: MIT
// Avalonia 11 entry point for the Editor.
//
// The Avalonia SDK's auto-generated Main was emitting CS5001 under
// dotnet 10 RC SDK when no .axaml generator wired the Program.Main delegate;
// explicit wins over auto-gen and gives us a single place to configure
// platform detection / Skia / tracing.
//
// StartWithClassicDesktopLifetime is used (not StartWithLifetime) so the
// existing MainWindow.OnClosed swapchain-tear-down flows continue to match
// the App.OnFrameworkInitializationCompleted path.

using System;
using Avalonia;

namespace Engine.Editor;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UseSkia()
                     .UsePlatformDetect()
                     .LogToTrace();
}
