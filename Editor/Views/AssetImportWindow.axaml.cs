using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Engine.Editor.ViewModels;
using static Engine.CBindings.Log;

namespace Engine.Editor.Views;

public partial class AssetImportWindow : Window
{
    public AssetImportWindow()
    {
        InitializeComponent();
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OnBrowseSourceClicked(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Model to Import",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("GLTF Models") { Patterns = new[] { "*.glb", "*.gltf" } }
            }
        });

        if (files.Count > 0 && DataContext is AssetImportViewModel vm)
        {
            vm.SourceFile = files[0].Path.LocalPath;
        }
    }



    private async void OnImportClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AssetImportViewModel vm) return;
        
        if (string.IsNullOrWhiteSpace(vm.SourceFile) || !File.Exists(vm.SourceFile))
        {
            vm.StatusMessage = "Please select a valid source file.";
            return;
        }
        
        string targetDirectory = Path.Combine(App.ProjectRoot, "Content");
        if (!Directory.Exists(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        vm.StatusMessage = "Cooking asset... Please wait.";

        // Run engine_cook
        var tcs = new TaskCompletionSource<bool>();
        // Find engine_cook executable robustly
        string? cookExe = null;
        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
        
        while (currentDir != null && currentDir.Length > 0)
        {
            string testPath = Path.Combine(currentDir, "engine_cook");
            if (File.Exists(testPath))
            {
                cookExe = testPath;
                break;
            }
            
            testPath = Path.Combine(currentDir, "out", "engine_cook");
            if (File.Exists(testPath))
            {
                cookExe = testPath;
                break;
            }
            
            var parent = Directory.GetParent(currentDir);
            if (parent == null || parent.FullName == currentDir) break;
            currentDir = parent.FullName;
        }

        if (cookExe == null || !File.Exists(cookExe))
        {
            string err = $"Error: engine_cook executable not found! Searched up from {AppDomain.CurrentDomain.BaseDirectory}";
            vm.StatusMessage = err;
            Error(err, "Editor");
            return;
        }

        // basisu ships next to engine_cook in the published .app bundle
        // (`Engine.app/Contents/MacOS/basisu` next to
        // `Engine.app/Contents/MacOS/engine_cook`, per scripts/build-mac-app.sh
        // stage 3). The most reliable resolution is "same dir as cookExe".
        // Pre-resolve and pass --basisu-path so Cook doesn't have to guess
        // through a 4-level ancestor walk that doesn't match bundle layout.
        string basisuPath = Path.Combine(Path.GetDirectoryName(cookExe) ?? string.Empty, "basisu");
        string basisuFlag = File.Exists(basisuPath) ? $" --basisu-path \"{basisuPath}\"" : string.Empty;

        var processInfo = new ProcessStartInfo
        {
            FileName = cookExe,
            Arguments = $"\"{vm.SourceFile}\" \"{targetDirectory}\" -scale {vm.ScaleX} {vm.ScaleY} {vm.ScaleZ}{basisuFlag}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            var process = Process.Start(processInfo);
            if (process != null)
            {
                await process.WaitForExitAsync();
                
                string error = await process.StandardError.ReadToEndAsync();
                string output = await process.StandardOutput.ReadToEndAsync();
                
                if (process.ExitCode != 0)
                {
                    string failMsg = $"Import failed (code {process.ExitCode}): {error}";
                    vm.StatusMessage = failMsg;
                    Error(failMsg, "Editor");
                }
                else
                {
                    vm.StatusMessage = "Import completed successfully!";
                    vm.ImportSucceeded = true;
                    vm.ImportedSceneName = Path.GetFileNameWithoutExtension(vm.SourceFile);
                    Info($"Import succeeded:\n{output}", "Editor");
                    // Wait a moment then close
                    await Task.Delay(1000);
                    Close();
                }
            }
        }
        catch (Exception ex)
        {
            string exMsg = $"Exception during import: {ex.Message}";
            vm.StatusMessage = exMsg;
            Error(exMsg, "Editor");
        }
    }
}
