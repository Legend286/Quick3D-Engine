// SPDX-License-Identifier: MIT
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Engine.Editor.ViewModels;

namespace Engine.Editor.Views;

public partial class WelcomeWindow : Window
{
    public WelcomeWindow()
    {
        InitializeComponent();
        DataContext = new WelcomeViewModel();
    }

    private void InitializeComponent()
    {
        Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }

    private async void OnBrowseTargetClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Target Directory",
            AllowMultiple = false
        });

        if (folders.Count > 0 && DataContext is WelcomeViewModel vm)
        {
            vm.TargetDirectory = folders[0].Path.LocalPath;
        }
    }

    private async void OnOpenProjectClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Open Project Folder",
            AllowMultiple = false
        });

        if (folders is { Count: > 0 })
        {
            var path = folders[0].Path.LocalPath;
            LaunchMainWindow(path);
        }
    }

    private void OnCreateProjectClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is WelcomeViewModel vm)
        {
            if (string.IsNullOrWhiteSpace(vm.ProjectName) || string.IsNullOrWhiteSpace(vm.TargetDirectory))
            {
                vm.StatusMessage = "Please provide a Project Name and Target Directory.";
                return;
            }

            string fullPath = Path.Combine(vm.TargetDirectory, vm.ProjectName);
            try
            {
                GenerateNewProject(fullPath, vm.ProjectName, vm.Organization);
                LaunchMainWindow(fullPath);
            }
            catch (System.Exception ex)
            {
                vm.StatusMessage = $"Error: {ex.Message}";
            }
        }
    }

    private void LaunchMainWindow(string projectRoot)
    {
        // 1. Initialize Logging
        EngineLogBootstrap.InitFromProject(projectRoot);

        // 2. Launch MainWindow
        var mainWindow = new MainWindow();

        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = mainWindow;
        }

        mainWindow.Show();
        this.Close();
    }

    private void GenerateNewProject(string newProjectPath, string projectName, string organization)
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
        string guid = System.Guid.NewGuid().ToString();

        WriteFileAtomic(Path.Combine(newProjectPath, ".eeproj", "project.json"),
$@"{{
  ""version"": 1,
  ""guid"": ""{guid}"",
  ""name"": ""{projectName}"",
  ""company"": ""{organization}"",
  ""steam_app_id"": 480,
  ""save_game_folder_name"": ""{projectName.Replace(" ", "")}"",
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
  },
  ""logging"": {
    ""log_mode"":              2,
    ""ring_capacity_records"": 1024,
    ""max_msg_bytes"":         512,
    ""enable_crash_dump"":     true,
    ""module_overrides"":      {}
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
            // Copy all content files
            string sourceContentDir = Path.Combine(App.EngineSourceRoot, "Content");
            if (Directory.Exists(sourceContentDir))
            {
                foreach (var file in Directory.GetFiles(sourceContentDir, "*", SearchOption.AllDirectories))
                {
                    string relPath = Path.GetRelativePath(sourceContentDir, file);
                    string destPath = Path.Combine(newProjectPath, "Content", relPath);
                    string destDir = Path.GetDirectoryName(destPath) ?? "";
                    if (!Directory.Exists(destDir))
                        Directory.CreateDirectory(destDir);

                    CopyFileAtomic(file, destPath);
                }
            }

            // Copy Game C# code files
            string sourceGameDir = Path.Combine(App.EngineSourceRoot, "Game");
            if (Directory.Exists(sourceGameDir))
            {
                foreach (var file in Directory.GetFiles(sourceGameDir))
                {
                    string ext = Path.GetExtension(file);
                    if (ext == ".cs" || ext == ".md")
                        CopyFileAtomic(file, Path.Combine(newProjectPath, "Game", Path.GetFileName(file)));
                }
            }

            // 4. Generate custom Engine.Game.csproj using absolute paths to the engine libraries
            string rhiCsproj = Path.Combine(App.EngineSourceRoot, "engine_cs", "Engine.RHI", "Engine.RHI.csproj");
            string graphCsproj = Path.Combine(App.EngineSourceRoot, "engine_cs", "Engine.RenderGraph", "Engine.RenderGraph.csproj");
            string sceneCsproj = Path.Combine(App.EngineSourceRoot, "engine_cs", "Engine.Scene", "Engine.Scene.csproj");
            string assetsCsproj = Path.Combine(App.EngineSourceRoot, "engine_cs", "Engine.Assets", "Engine.Assets.csproj");
            string cbindingsCsproj = Path.Combine(App.EngineSourceRoot, "OutOfBand", "Engine.CBindings", "Engine.CBindings.csproj");

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
    <ProjectReference Include=""{assetsCsproj}"" />
    <ProjectReference Include=""{cbindingsCsproj}"" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include=""Twizzle.ImGui-Bundle.NET"" Version=""1.91.5.2"" />
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
        if (!File.Exists(sourcePath)) return;
        string tmpPath = destPath + ".tmp";
        File.Copy(sourcePath, tmpPath, overwrite: true);
        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tmpPath, destPath);
    }
}
