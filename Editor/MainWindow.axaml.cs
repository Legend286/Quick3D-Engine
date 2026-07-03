// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Engine.CBindings;
using Engine.Editor.ViewModels;

namespace Engine.Editor;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
        Opened += OnOpened;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    // Window.Opened fires once, after the window is fully shown AND its
    // children have been laid out. This is the first moment the host Window
    // resolves via TopLevel.GetTopLevel - earlier lifecycle hooks on the
    // ViewportPanelView ran before the visual subtree connected and Metal
    // init aborted with 'Viewport host is not a Window'.
    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.ViewportVm is not null)
            vm.ViewportVm.AttachToVisualTree(this);
    }

    private void OnExitClicked(object? sender, RoutedEventArgs e) => Close();

    private void OnToggleConsoleClicked(object? sender, RoutedEventArgs e)
    {
        var consoles = this.FindControl<TabControl>("ConsolesTabControl");
        var icon = this.FindControl<TextBlock>("ConsoleCollapseIcon");
        if (consoles is not null && icon is not null)
        {
            consoles.IsVisible = !consoles.IsVisible;
            icon.Text = consoles.IsVisible ? "\ue313" : "\ue316";
        }
    }

    private void OnHotReloadClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.ViewportVm is not null)
        {
            vm.ViewportVm.HotReload();
        }
    }

    private void OnOpenProjectClicked(object? sender, RoutedEventArgs e)
    {
        var welcome = new Views.WelcomeWindow();
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = welcome;
        }
        welcome.Show();
        this.Close();
    }

    private void OnNewSceneClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.ViewportVm is not null)
        {
            vm.ViewportVm.NewScene();
        }
    }

    private void OnSaveSceneClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.ViewportVm is not null)
        {
            if (vm.ViewportVm.CurrentSceneName == "New Scene")
                OnSaveSceneAsClicked(sender, e);
            else
                vm.ViewportVm.SaveScene();
        }
    }

    private async void OnSaveSceneAsClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Scene As",
            DefaultExtension = ".scene.json",
            FileTypeChoices = new[] { new FilePickerFileType("Scene JSON") { Patterns = new[] { "*.scene.json" } } }
        });
        if (file is not null)
        {
            if (DataContext is MainWindowViewModel vm && vm.ViewportVm is not null)
            {
                string name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file.Path.LocalPath));
                vm.ViewportVm.SaveSceneAs(name);
            }
        }
    }

    private async void OnOpenSceneClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Scene",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Scene JSON") { Patterns = new[] { "*.scene.json" } } }
        });
        if (files is { Count: > 0 })
        {
            if (DataContext is MainWindowViewModel vm && vm.ViewportVm is not null)
            {
                string name = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(files[0].Path.LocalPath));
                vm.ViewportVm.LoadScene(name);
            }
        }
    }

    private async void OnImportAssetClicked(object? sender, RoutedEventArgs e)
    {
        var vm = new ViewModels.AssetImportViewModel();
        var importWindow = new Views.AssetImportWindow
        {
            DataContext = vm
        };
        await importWindow.ShowDialog(this);
        
        if (vm.ImportSucceeded && !string.IsNullOrEmpty(vm.ImportedSceneName))
        {
            if (DataContext is MainWindowViewModel mainVm && mainVm.ViewportVm is not null)
            {
                mainVm.ViewportVm.AddModelToScene(vm.ImportedSceneName + ".mdl");
            }
        }
    }

    private void OnNewProjectClicked(object? sender, RoutedEventArgs e)
    {
        var welcome = new Views.WelcomeWindow();
        if (welcome.DataContext is ViewModels.WelcomeViewModel vm)
            vm.IsNewProjectMode = true;

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = welcome;
        }
        welcome.Show();
        this.Close();
    }

    /// <summary>
    /// If the project's modules.json exists but is missing the "logging" block,
    /// inject it so the console panel receives verbose log output.
    /// Uses a regex-replace so the JSON stays valid without a full parse+serialize round-trip.
    /// </summary>
    private static void EnsureLoggingBlock(string projectPath)
    {
        string modulesPath = Path.Combine(projectPath, ".eeproj", "modules.json");
        if (!File.Exists(modulesPath)) return;

        string text = File.ReadAllText(modulesPath);
        if (text.Contains("\"logging\"")) return;

        const string loggingBlock =
            ",\n  \"logging\": {\n" +
            "    \"log_mode\":              2,\n" +
            "    \"ring_capacity_records\": 1024,\n" +
            "    \"max_msg_bytes\":         512,\n" +
            "    \"enable_crash_dump\":     true,\n" +
            "    \"module_overrides\":      {}\n" +
            "  }";

        int lastBrace = text.LastIndexOf('}');
        if (lastBrace < 0) return;

        string patched = text.Insert(lastBrace, loggingBlock + "\n");
        WriteFileAtomic(modulesPath, patched);
        Log.Info($"[Editor] Migrated modules.json to add logging block: {modulesPath}", "Editor");
    }

    private static void WriteFileAtomic(string filePath, string content)
    {
        string dir = Path.GetDirectoryName(filePath) ?? "";
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string tmpPath = filePath + ".tmp";
        using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs, System.Text.Encoding.UTF8))
        {
            writer.Write(content);
            writer.Flush();
            fs.Flush(true);
        }
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        File.Move(tmpPath, filePath);
    }

    protected override void OnClosed(System.EventArgs e)
    {
        // Release the Metal swapchain + device before tearing down the logger.
        if (DataContext is MainWindowViewModel vm)
        {
            vm.ViewportVm?.DisposeOnClose();
            vm.ConsoleVm?.DisposeOnClose();
        }
        EngineLog.EngineLogShutdown();
        base.OnClosed(e);
    }
}
