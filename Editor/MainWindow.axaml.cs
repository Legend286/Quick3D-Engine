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

    private async void OnOpenProjectClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Project Folder",
            AllowMultiple = false
        });

        if (folders is { Count: > 0 })
        {
            var path = folders[0].Path.LocalPath;
            if (DataContext is MainWindowViewModel vm && vm.ViewportVm is not null)
            {
                vm.ViewportVm.ReloadProject(path);
            }
        }
    }

    private async void OnNewProjectClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Directory for New Project",
            AllowMultiple = false
        });

        if (folders is { Count: > 0 })
        {
            var path = folders[0].Path.LocalPath;
            try
            {
                GenerateNewProject(path);
                if (DataContext is MainWindowViewModel vm && vm.ViewportVm is not null)
                {
                    vm.ViewportVm.ReloadProject(path);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[NewProject] Failed to generate project: {ex.Message}", "Editor");
            }
        }
    }

    private void GenerateNewProject(string newProjectPath)
    {
        // 1. Create directories
        Directory.CreateDirectory(Path.Combine(newProjectPath, ".eeproj"));
        Directory.CreateDirectory(Path.Combine(newProjectPath, "Content"));
        Directory.CreateDirectory(Path.Combine(newProjectPath, "Content", "scenes"));
        Directory.CreateDirectory(Path.Combine(newProjectPath, "Content", "shaders"));
        Directory.CreateDirectory(Path.Combine(newProjectPath, "Content", "models"));
        Directory.CreateDirectory(Path.Combine(newProjectPath, "Content", "materials"));
        Directory.CreateDirectory(Path.Combine(newProjectPath, "Content", "textures"));
        Directory.CreateDirectory(Path.Combine(newProjectPath, "Content", "sounds"));
        Directory.CreateDirectory(Path.Combine(newProjectPath, "Content", "scripts"));
        Directory.CreateDirectory(Path.Combine(newProjectPath, "Game"));

        // 2. Write .eeproj sub-files atomically
        string guid = Guid.NewGuid().ToString();

        WriteFileAtomic(Path.Combine(newProjectPath, ".eeproj", "project.json"),
$@"{{
  ""version"": 1,
  ""guid"": ""{guid}"",
  ""name"": ""New Game"",
  ""company"": ""My Company"",
  ""steam_app_id"": 480,
  ""save_game_folder_name"": ""NewGame"",
  ""network_tick_rate"": 60,
  ""external_mounts"": []
}}");

        WriteFileAtomic(Path.Combine(newProjectPath, ".eeproj", "scenes.json"),
@"{
  ""version"": 1,
  ""startup_scene"":   ""Content/scenes/hello.scene.json"",
  ""main_menu_scene"": ""Content/scenes/hello.scene.json"",
  ""cooked_scenes"": [
    ""Content/scenes/hello.scene.json""
  ]
}");

        WriteFileAtomic(Path.Combine(newProjectPath, ".eeproj", "modules.json"),
@"{
  ""version"": 1,
  ""renderer"": {
    ""default_pipeline"":   ""mesh_shader_forward_plus"",
    ""msaa"":               4,
    ""post_aa"":            ""smaa"",
    ""tonemapper"":         ""aces_filmic"",
    ""hdr"":                true,
    ""shadow_resolution"":  2048,
    ""lightmap_format"":    ""rg16f""
  },
  ""physics"": {
    ""engine"":             ""jolt"",
    ""fixed_timestep_hz"":  60,
    ""max_substeps"":       4,
    ""gravity"":            [0, -9.81, 0],
    ""default_layer_count"": 32
  },
  ""audio"": {
    ""backend"":  ""miniaudio_steam_audio"",
    ""hrtf"":     true,
    ""buses"":    [""Master"", ""Music"", ""SFX"", ""Voice"", ""Ambience""],
    ""default_master_volume"": 0.85
  }
}");

        WriteFileAtomic(Path.Combine(newProjectPath, ".eeproj", "addons.json"),
@"{
  ""version"": 1,
  ""enabled"": []
}");

        WriteFileAtomic(Path.Combine(newProjectPath, ".eeproj", "input.json"),
@"{
  ""version"": 1,
  ""actions"": {
    ""jump"":         { ""primary"": ""Space"",         ""gamepad"": ""GamepadSouth"" },
    ""move_forward"": { ""primary"": ""KeyW"",          ""axis"": { ""value"":  1 } },
    ""move_back"":    { ""primary"": ""KeyS"",          ""axis"": { ""value"": -1 } },
    ""fire_primary"": { ""primary"": ""MouseLeftBtn"", ""gamepad"": ""GamepadRight"" }
  }
}");

        WriteFileAtomic(Path.Combine(newProjectPath, ".eeproj", "locales.json"),
@"{
  ""version"": 1,
  ""default_locale"":    ""en-US"",
  ""supported_locales"": [""en-US""],
  ""fallback_chain"":    [""en-US""]
}");

        WriteFileAtomic(Path.Combine(newProjectPath, ".gitignore"),
@".eeproj/editor.local.json
out/
bin/
obj/
");

        // 3. Copy templates from App.EngineSourceRoot
        if (!string.IsNullOrEmpty(App.EngineSourceRoot))
        {
            CopyFileAtomic(
                Path.Combine(App.EngineSourceRoot, "Content", "scenes", "hello.scene.json"),
                Path.Combine(newProjectPath, "Content", "scenes", "hello.scene.json")
            );
            CopyFileAtomic(
                Path.Combine(App.EngineSourceRoot, "Content", "shaders", "triangle.metal"),
                Path.Combine(newProjectPath, "Content", "shaders", "triangle.metal")
            );

            // Copy Game C# code files
            CopyFileAtomic(Path.Combine(App.EngineSourceRoot, "Game", "GameLoop.cs"), Path.Combine(newProjectPath, "Game", "GameLoop.cs"));
            CopyFileAtomic(Path.Combine(App.EngineSourceRoot, "Game", "HelloTrianglePass.cs"), Path.Combine(newProjectPath, "Game", "HelloTrianglePass.cs"));
            CopyFileAtomic(Path.Combine(App.EngineSourceRoot, "Game", "Program.cs"), Path.Combine(newProjectPath, "Game", "Program.cs"));
            CopyFileAtomic(Path.Combine(App.EngineSourceRoot, "Game", "Renderer.cs"), Path.Combine(newProjectPath, "Game", "Renderer.cs"));
            CopyFileAtomic(Path.Combine(App.EngineSourceRoot, "Game", "README.md"), Path.Combine(newProjectPath, "Game", "README.md"));

            // 4. Generate custom Engine.Game.csproj using absolute paths to the engine libraries
            string rhiCsproj = Path.Combine(App.EngineSourceRoot, "engine_cs", "Engine.RHI", "Engine.RHI.csproj");
            string graphCsproj = Path.Combine(App.EngineSourceRoot, "engine_cs", "Engine.RenderGraph", "Engine.RenderGraph.csproj");
            string sceneCsproj = Path.Combine(App.EngineSourceRoot, "engine_cs", "Engine.Scene", "Engine.Scene.csproj");

            string csprojContent =
$@"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <RootNamespace>Engine.Game</RootNamespace>
    <AssemblyName>Engine.Game</AssemblyName>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include=""{rhiCsproj}"" />
    <ProjectReference Include=""{graphCsproj}"" />
    <ProjectReference Include=""{sceneCsproj}"" />
  </ItemGroup>
</Project>
";
            WriteFileAtomic(Path.Combine(newProjectPath, "Game", "Engine.Game.csproj"), csprojContent);
        }
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

    private static void CopyFileAtomic(string sourcePath, string destPath)
    {
        if (File.Exists(sourcePath))
        {
            string content = File.ReadAllText(sourcePath);
            WriteFileAtomic(destPath, content);
        }
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
