// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
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
        KeyDown += OnWindowKeyDown;
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
        {
            vm.ViewportVm.AttachToVisualTree(this);
            vm.ViewportVm.OnPluginEnableRequested +=
                OnPluginEnableRequested;
            Services.PluginCatalogService.Shared
                .ShaderReloadRequested +=
                vm.ViewportVm.ReloadPluginShaders;
            Services.PluginCatalogService.Shared
                .CodeReloadRequested +=
                vm.ViewportVm.ReloadPluginCode;
            Services.PluginCatalogService.Shared
                .AvailabilityChanged +=
                vm.ViewportVm.RefreshPluginAvailability;
        }
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

    private void OnPluginsClicked(
        object? sender,
        RoutedEventArgs e)
    {
        var window = new Views.PluginsWindow();
        window.Show(this);
    }

    private async void OnPluginEnableRequested(
        string pluginId)
    {
        var prompt =
            new Views.PluginEnablePromptWindow();
        Views.PluginEnablePromptResult result =
            await prompt.ShowDialog<
                Views.PluginEnablePromptResult>(this);
        if (result ==
            Views.PluginEnablePromptResult.Enable)
        {
            if (Services.PluginCatalogService.Shared
                    .Enable(pluginId) &&
                DataContext is MainWindowViewModel vm &&
                vm.ViewportVm != null)
            {
                vm.ViewportVm
                    .EnablePathTracingRenderer();
            }
        }
        else if (result ==
                 Views.PluginEnablePromptResult
                     .OpenPlugins)
        {
            var plugins = new Views.PluginsWindow();
            plugins.Show(this);
        }
    }

    private void OnOpenProjectClicked(object? sender, RoutedEventArgs e)
    {
        var welcome = new Views.WelcomeWindow(this);
        welcome.Show();
        welcome.Activate();
    }

    private void OnNewSceneClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.ViewportVm is not null)
        {
            vm.Commands.Clear();
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
        var scenesFolder =
            await GetScenesFolderAsync(topLevel);
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Scene As",
            DefaultExtension = ".scene.json",
            SuggestedStartLocation = scenesFolder,
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

    private void OnCreateEmptyEntityClicked(
        object? sender,
        RoutedEventArgs e)
    {
        CreateAndSelect(
            static viewport => viewport.AddEmptyEntity(),
            "Create Empty Entity");
    }

    private void OnCreateCameraClicked(
        object? sender,
        RoutedEventArgs e)
    {
        CreateAndSelect(
            static viewport => viewport.AddCamera(),
            "Create Camera");
    }

    private void OnAddDirectionalLightClicked(
        object? sender,
        RoutedEventArgs e)
    {
        CreateAndSelect(
            static viewport =>
                viewport.AddDirectionalLight(),
            "Create Directional Light");
    }

    private void OnAddPointLightClicked(
        object? sender,
        RoutedEventArgs e)
    {
        CreateAndSelect(
            static viewport => viewport.AddPointLight(),
            "Create Point Light");
    }

    private void OnAddSpotLightClicked(
        object? sender,
        RoutedEventArgs e)
    {
        CreateAndSelect(
            static viewport => viewport.AddSpotLight(),
            "Create Spot Light");
    }

    private void CreateAndSelect(
        Func<ViewportPanelViewModel, ulong> create,
        string commandName)
    {
        if (DataContext is not MainWindowViewModel vm ||
            vm.ViewportVm is null)
        {
            return;
        }

        ulong ent = create(vm.ViewportVm);
        if (ent == 0) return;

        vm.RecordCreatedEntity(ent, commandName);
        vm.HierarchyVm.SelectEntity(ent);
        vm.InspectorVm.SetSelectedEntity(ent);
        vm.ViewportVm.GameLoop?.SetSelectedEntity(ent);
        vm.ViewportVm.RequestRender();
    }

    private void OnDeleteEntityClicked(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.DeleteSelectedEntity();
    }

    private void OnUndoClicked(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.Undo();
    }

    private void OnRedoClicked(
        object? sender,
        RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.Redo();
    }

    private void OnWindowKeyDown(
        object? sender,
        KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
            return;

        bool commandModifier =
            (e.KeyModifiers &
             (KeyModifiers.Control |
              KeyModifiers.Meta)) != 0;
        if (commandModifier && e.Key == Key.Z)
        {
            if ((e.KeyModifiers &
                 KeyModifiers.Shift) != 0)
            {
                vm.Redo();
            }
            else
            {
                vm.Undo();
            }
            e.Handled = true;
            return;
        }
        if (commandModifier && e.Key == Key.Y)
        {
            vm.Redo();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Delete or Key.Back)
        {
            vm.DeleteSelectedEntity();
            e.Handled = true;
        }
    }

    private async void OnOpenSceneClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;
        var scenesFolder =
            await GetScenesFolderAsync(topLevel);
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Scene",
            AllowMultiple = false,
            SuggestedStartLocation = scenesFolder,
            FileTypeFilter = new[] { new FilePickerFileType("Scene JSON") { Patterns = new[] { "*.scene.json" } } }
        });
        if (files is { Count: > 0 })
            OpenSceneFromPath(files[0].Path.LocalPath);
    }

    internal void OpenSceneFromPath(string scenePath)
    {
        if (DataContext is not MainWindowViewModel vm ||
            vm.ViewportVm is null)
        {
            return;
        }

        string scenesDirectory = Path.GetFullPath(
            Path.Combine(App.ProjectRoot, "Content", "scenes"));
        string fullPath = Path.GetFullPath(scenePath);
        string relativePath = Path.GetRelativePath(
            scenesDirectory,
            fullPath);
        string sceneName =
            relativePath.StartsWith("..", StringComparison.Ordinal)
                ? Path.GetFileName(fullPath)
                : relativePath;
        if (sceneName.EndsWith(
                ".scene.json",
                StringComparison.OrdinalIgnoreCase))
        {
            sceneName = sceneName[..^".scene.json".Length];
        }

        vm.Commands.Clear();
        vm.ViewportVm.LoadScene(
            sceneName.Replace(
                Path.DirectorySeparatorChar,
                '/'));
    }

    private static async System.Threading.Tasks.Task<IStorageFolder?>
        GetScenesFolderAsync(TopLevel topLevel)
    {
        string scenesDirectory =
            Path.Combine(App.ProjectRoot, "Content", "scenes");
        if (!Directory.Exists(scenesDirectory))
            Directory.CreateDirectory(scenesDirectory);
        return await topLevel.StorageProvider
            .TryGetFolderFromPathAsync(scenesDirectory);
    }

    private async void OnImportAssetClicked(object? sender, RoutedEventArgs e)
    {
        var vm = new ViewModels.AssetImportViewModel();
        var importWindow = new Views.AssetImportWindow
        {
            DataContext = vm
        };
        await importWindow.ShowDialog(this);
    }

    private void OnNewProjectClicked(object? sender, RoutedEventArgs e)
    {
        var welcome = new Views.WelcomeWindow(this);
        if (welcome.DataContext is ViewModels.WelcomeViewModel vm)
            vm.IsNewProjectMode = true;
        welcome.Show();
        welcome.Activate();
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
            vm.RenderGraphVm?.Dispose();
            vm.ViewportVm?.DisposeOnClose();
            vm.ConsoleVm?.DisposeOnClose();
        }
        Services.PluginCatalogService.Shared.Dispose();
        EngineLog.EngineLogShutdown();
        base.OnClosed(e);
    }
}
