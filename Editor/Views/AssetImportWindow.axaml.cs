using System;
using System.IO;
using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Engine.Editor.ViewModels;

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
            Title = "Select Model or Texture to Import",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("All Supported Assets") { Patterns = new[] { "*.glb", "*.gltf", "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.ktx2" } },
                new FilePickerFileType("GLTF Models") { Patterns = new[] { "*.glb", "*.gltf" } },
                new FilePickerFileType("Textures") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.tga", "*.bmp", "*.ktx2" } }
            }
        });

        if (files.Count > 0 && DataContext is AssetImportViewModel vm)
        {
            vm.SourceFile = files[0].Path.LocalPath;
            string ext = Path.GetExtension(vm.SourceFile).ToLower();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".tga" || ext == ".bmp" || ext == ".ktx2")
                vm.AssetType = "Texture";
            else
                vm.AssetType = "Model";

            if (vm.AssetType == "Model")
                await InspectSourceAsync(vm);
        }
    }

    private static async Task InspectSourceAsync(AssetImportViewModel vm)
    {
        vm.IsInspecting = true;
        vm.InspectionMessage = "Inspecting source animations...";
        try
        {
            AssetImportInspection inspection =
                await Services.AssetImportService.Shared
                    .InspectAsync(vm.SourceFile);
            vm.ApplyInspection(inspection);
        }
        catch (Exception ex)
        {
            vm.ClearInspection($"Inspection failed: {ex.Message}");
        }
        finally
        {
            vm.IsInspecting = false;
        }
    }

    private void OnImportClicked(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AssetImportViewModel vm) return;

        if (string.IsNullOrWhiteSpace(vm.SourceFile) || !File.Exists(vm.SourceFile))
        {
            vm.StatusMessage = "Please select a valid source file.";
            return;
        }

        if (vm.AssetType == "Model" &&
            !vm.ImportMesh &&
            !vm.ImportSkeleton &&
            vm.SelectedAnimationNames().Count == 0)
        {
            vm.StatusMessage = "Select mesh, skeleton, or at least one animation.";
            return;
        }

        if (!Services.AssetImportService.Shared.TryStart(
                vm.SourceFile,
                vm.TargetDirectory,
                vm.AssetType,
                vm.ScaleX,
                vm.ScaleY,
                vm.ScaleZ,
                vm.ImportMesh,
                vm.ImportSkeleton,
                vm.ImportMaterials,
                vm.ImportTextures,
                vm.SelectedAnimationNames()))
        {
            vm.StatusMessage = "Another asset import is already running.";
            return;
        }

        Close();
    }
}
