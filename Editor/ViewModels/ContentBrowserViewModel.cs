// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using System.Threading.Tasks;

namespace Engine.Editor.ViewModels;

public partial class ContentFolder : ObservableObject
{
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _fullPath;
    public ObservableCollection<ContentFolder> SubFolders { get; } = new();

    public ContentFolder(string name, string fullPath)
    {
        Name = name;
        FullPath = fullPath;
    }
}

public partial class ContentAsset : ObservableObject
{
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _fullPath;
    [ObservableProperty] private string _assetType;
    [ObservableProperty] private string _iconGlyph;
    [ObservableProperty] private Bitmap? _thumbnailBitmap;
    [ObservableProperty] private bool _isExpanded;

    public int ModelPartIndex { get; }
    public int ModelPartCount { get; }
    public bool IsModelPart => ModelPartIndex >= 0;
    public bool CanExpand =>
        AssetType == "Model" && ModelPartCount > 0;
    public string PreviewAssetType =>
        IsModelPart ? "Model" : AssetType;
    public string ThumbnailIdentity =>
        $"{FullPath}|{ModelPartIndex}";
    public string ExpansionGlyph =>
        IsExpanded ? "\uE5CE" : "\uE5CF";
    public string IconColor =>
        AssetType == "Scene" ? "#7AA2F7" : "#AAB0B6";

    public ContentAsset(
        string name,
        string fullPath,
        string assetType,
        string iconGlyph,
        int modelPartIndex = -1,
        int modelPartCount = 0,
        bool isExpanded = false)
    {
        Name = name;
        FullPath = fullPath;
        AssetType = assetType;
        IconGlyph = iconGlyph;
        ModelPartIndex = modelPartIndex;
        ModelPartCount = modelPartCount;
        IsExpanded = isExpanded;
    }

    partial void OnIsExpandedChanged(bool value)
        => OnPropertyChanged(nameof(ExpansionGlyph));
}

public partial class ContentBrowserViewModel : ObservableObject, IDisposable
{
    private const int HoverPreviewDelayMs = 180;
    private const int HoverPreviewRenderSize = 512;

    [ObservableProperty] private ObservableCollection<ContentFolder> _rootFolders = new();
    [ObservableProperty] private ContentFolder? _selectedFolder;
    [ObservableProperty] private ObservableCollection<ContentAsset> _currentAssets = new();
    [ObservableProperty] private Bitmap? _hoverPreviewBitmap;
    [ObservableProperty] private bool _hoverPreviewVisible;
    [ObservableProperty] private bool _hoverPreviewShowImage;
    [ObservableProperty] private bool _hoverPreviewShowLive;
    [ObservableProperty] private string _hoverPreviewTitle = string.Empty;
    [ObservableProperty] private string _hoverPreviewAssetType = string.Empty;
    [ObservableProperty] private double _hoverPreviewLeft;
    [ObservableProperty] private double _hoverPreviewTop;
    [ObservableProperty] private ContentAsset? _hoverPreviewAsset;

    private FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _hoverPreviewTimer;
    private ContentAsset? _pendingHoverAsset;
    private ContentAsset? _activeHoverAsset;
    private int _hoverRequestId;
    private readonly HashSet<string> _expandedModelPaths =
        new(StringComparer.OrdinalIgnoreCase);

    public ContentBrowserViewModel()
    {
        _hoverPreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoverPreviewDelayMs) };
        _hoverPreviewTimer.Tick += OnHoverPreviewTimerTick;
        InitializeFolders();
        SetupWatcher();
    }

    public void BeginAssetHover(ContentAsset asset, double left, double top)
    {
        _pendingHoverAsset = asset;
        _hoverRequestId++;
        HoverPreviewLeft = left;
        HoverPreviewTop = top;
        _hoverPreviewTimer.Stop();
        _hoverPreviewTimer.Start();
    }

    public void UpdateAssetHoverPosition(double left, double top)
    {
        HoverPreviewLeft = left;
        HoverPreviewTop = top;
    }

    public void EndAssetHover(ContentAsset? asset = null)
    {
        if (asset != null && _pendingHoverAsset != asset && _activeHoverAsset != asset)
            return;

        _hoverPreviewTimer.Stop();
        _pendingHoverAsset = null;
        _activeHoverAsset = null;
        HoverPreviewVisible = false;
        HoverPreviewShowImage = false;
        HoverPreviewShowLive = false;
        HoverPreviewAsset = null;
        HoverPreviewBitmap = null;
    }

    private async void OnHoverPreviewTimerTick(object? sender, EventArgs e)
    {
        _hoverPreviewTimer.Stop();
        if (_pendingHoverAsset == null)
            return;

        var asset = _pendingHoverAsset;
        _activeHoverAsset = asset;
        HoverPreviewAsset = asset;
        HoverPreviewTitle = asset.Name;
        HoverPreviewAssetType = asset.PreviewAssetType;
        HoverPreviewVisible = true;
        HoverPreviewShowLive = false;
        HoverPreviewShowImage = true;
        HoverPreviewBitmap = asset.ThumbnailBitmap;

        if (asset.PreviewAssetType == "Model" ||
            asset.PreviewAssetType == "Material")
            return;

        int requestId = _hoverRequestId;
        var preview =
            await Services.ThumbnailGenerator.GetOrGenerateThumbnailAsync(
                asset.FullPath,
                asset.PreviewAssetType,
                HoverPreviewRenderSize,
                asset.ModelPartIndex);
        if (requestId != _hoverRequestId || _activeHoverAsset != asset || preview == null)
            return;

        HoverPreviewBitmap = preview;
        HoverPreviewVisible = true;
    }

    private void InitializeFolders()
    {
        var contentDir = Path.GetFullPath("Content");
        var gameDir = Path.GetFullPath("Game");

        RootFolders.Clear();

        if (Directory.Exists(contentDir))
            RootFolders.Add(BuildFolderTree(contentDir, "Content"));

        if (Directory.Exists(gameDir))
            RootFolders.Add(BuildFolderTree(gameDir, "Game"));

        if (RootFolders.Count > 0)
        {
            SelectedFolder = RootFolders[0];
        }
    }

    private ContentFolder BuildFolderTree(string path, string name)
    {
        var folder = new ContentFolder(name, path);
        try
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                var dirName = Path.GetFileName(dir);
                folder.SubFolders.Add(BuildFolderTree(dir, dirName));
            }
        }
        catch { /* Ignore access denied */ }
        return folder;
    }

    partial void OnSelectedFolderChanged(ContentFolder? oldValue, ContentFolder? newValue)
    {
        EndAssetHover();
        LoadAssetsForFolder(newValue);
    }

    public void ToggleModelExpansion(ContentAsset asset)
    {
        if (!asset.CanExpand)
            return;
        if (!_expandedModelPaths.Add(asset.FullPath))
            _expandedModelPaths.Remove(asset.FullPath);
        LoadAssetsForFolder(SelectedFolder);
    }

    private void LoadAssetsForFolder(ContentFolder? folder)
    {
        EndAssetHover();
        var existingThumbnails = CurrentAssets
            .Where(asset => asset.ThumbnailBitmap != null)
            .GroupBy(asset => asset.ThumbnailIdentity)
            .ToDictionary(
                group => group.Key,
                group => group.First().ThumbnailBitmap);

        CurrentAssets.Clear();
        if (folder == null || !Directory.Exists(folder.FullPath)) return;

        try
        {
            var files = Directory.GetFiles(folder.FullPath);
            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLower();
                if (ext == ".json" && file.EndsWith(".scene.json")) ext = ".scene.json";

                string type = "";
                string icon = "\uE869"; // default file

                switch (ext)
                {
                    case ".mdl":
                        type = "Model";
                        icon = "\uE8B2"; // some 3d box icon
                        break;
                    case ".mat":
                        type = "Material";
                        icon = "\uE3C9"; // palette icon
                        break;
                    case ".ktx2":
                        type = "Texture";
                        icon = "\uE3F4"; // image icon
                        break;
                    case ".scene.json":
                        type = "Scene";
                        icon = "\uE3F7";
                        break;
                    default:
                        continue; // Skip unrecognized
                }

                int partCount = 0;
                Engine.Assets.ModelDefinition? modelDefinition = null;
                if (type == "Model")
                {
                    try
                    {
                        modelDefinition =
                            Engine.Assets.ModelLoader.ReadDefinition(file);
                        partCount = modelDefinition.Parts.Length;
                    }
                    catch
                    {
                    }
                }

                bool isExpanded =
                    _expandedModelPaths.Contains(file);
                var asset = new ContentAsset(
                    Path.GetFileName(file),
                    file,
                    type,
                    icon,
                    modelPartCount: partCount,
                    isExpanded: isExpanded);
                AddAsset(asset, existingThumbnails);

                if (!isExpanded || modelDefinition == null)
                    continue;
                for (int partIndex = 0;
                     partIndex < modelDefinition.Parts.Length;
                     ++partIndex)
                {
                    Engine.Assets.ModelPartDefinition definition =
                        modelDefinition.Parts[partIndex];
                    string partName =
                        !string.IsNullOrWhiteSpace(definition.Name)
                            ? definition.Name
                            : Path.GetFileNameWithoutExtension(
                                definition.Mesh);
                    if (string.IsNullOrWhiteSpace(partName))
                        partName = $"Part {partIndex + 1}";
                    var partAsset = new ContentAsset(
                        partName,
                        file,
                        "Model Part",
                        "\uE1B0",
                        modelPartIndex: partIndex);
                    AddAsset(partAsset, existingThumbnails);
                }
            }
        }
        catch { }
    }

    private void AddAsset(
        ContentAsset asset,
        IReadOnlyDictionary<string, Bitmap?> existingThumbnails)
    {
        if (existingThumbnails.TryGetValue(
                asset.ThumbnailIdentity,
                out Bitmap? existingBitmap))
        {
            asset.ThumbnailBitmap = existingBitmap;
        }
        else if (asset.PreviewAssetType is
                 "Model" or "Material" or "Texture")
        {
            string cacheFile =
                Services.ThumbnailGenerator.GetCacheFilePath(
                    asset.FullPath,
                    asset.PreviewAssetType,
                    modelPartIndex: asset.ModelPartIndex);
            if (File.Exists(cacheFile))
            {
                try
                {
                    asset.ThumbnailBitmap = new Bitmap(cacheFile);
                }
                catch
                {
                }
            }
        }

        CurrentAssets.Add(asset);
        if (asset.PreviewAssetType is not
                ("Model" or "Material" or "Texture") ||
            asset.ThumbnailBitmap != null)
        {
            return;
        }

        Task.Run(async () =>
        {
            Bitmap? bitmap =
                await Services.ThumbnailGenerator
                    .GetOrGenerateThumbnailAsync(
                        asset.FullPath,
                        asset.PreviewAssetType,
                        modelPartIndex: asset.ModelPartIndex);
            if (bitmap != null)
            {
                Dispatcher.UIThread.Post(
                    () => asset.ThumbnailBitmap = bitmap);
            }
        });
    }

    private FileSystemWatcher? _contentWatcher;
    private FileSystemWatcher? _gameWatcher;

    private void SetupWatcher()
    {
        var contentDir = Path.GetFullPath("Content");
        if (Directory.Exists(contentDir))
        {
            _contentWatcher = new FileSystemWatcher(contentDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
            };
            _contentWatcher.Created += OnFileSystemChanged;
            _contentWatcher.Deleted += OnFileSystemChanged;
            _contentWatcher.Renamed += OnFileSystemChanged;
            _contentWatcher.EnableRaisingEvents = true;
        }

        var gameDir = Path.GetFullPath("Game");
        if (Directory.Exists(gameDir))
        {
            _gameWatcher = new FileSystemWatcher(gameDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
            };
            _gameWatcher.Created += OnFileSystemChanged;
            _gameWatcher.Deleted += OnFileSystemChanged;
            _gameWatcher.Renamed += OnFileSystemChanged;
            _gameWatcher.EnableRaisingEvents = true;
        }
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        // Ignore cache directories
        if (e.FullPath.Contains(".cache") || e.FullPath.Contains("/out/")) return;

        bool isDirectoryChange = false;
        try
        {
            if (e.ChangeType == WatcherChangeTypes.Deleted)
            {
                // Deletion doesn't let us easily check if it was a directory using File.GetAttributes.
                // We'll guess based on lack of extension.
                isDirectoryChange = string.IsNullOrEmpty(Path.GetExtension(e.FullPath));
            }
            else
            {
                isDirectoryChange = File.GetAttributes(e.FullPath).HasFlag(FileAttributes.Directory);
            }
        }
        catch { }

        Dispatcher.UIThread.Post(() =>
        {
            if (isDirectoryChange)
            {
                // Preserve the expanded state by only modifying the tree if needed,
                // or for now, just don't rebuild the entire tree for file changes.
                // Actually, building the tree every time a folder changes will still collapse it.
                // To do this right, we would recursively update existing items.
                // For now, if we rebuild, at least we do it less often.
                var oldSelectedPath = SelectedFolder?.FullPath;
                InitializeFolders();

                if (oldSelectedPath != null)
                {
                    var folder = FindFolderByPath(RootFolders, oldSelectedPath);
                    if (folder != null)
                        SelectedFolder = folder;
                }
            }
            else if (SelectedFolder != null)
            {
                // If a file changed, and it belongs to the selected folder, refresh assets
                var selectedDir = SelectedFolder.FullPath;
                var changedDir = Path.GetDirectoryName(e.FullPath);
                if (string.Equals(selectedDir, changedDir, StringComparison.OrdinalIgnoreCase))
                {
                    LoadAssetsForFolder(SelectedFolder);
                }
            }
        });
    }

    private ContentFolder? FindFolderByPath(ObservableCollection<ContentFolder> folders, string path)
    {
        foreach (var f in folders)
        {
            if (f.FullPath == path) return f;
            var found = FindFolderByPath(f.SubFolders, path);
            if (found != null) return found;
        }
        return null;
    }

    public void MoveItem(string sourcePath, string targetDirectoryPath)
    {
        if (string.IsNullOrEmpty(sourcePath) || string.IsNullOrEmpty(targetDirectoryPath)) return;
        if (!Directory.Exists(targetDirectoryPath)) return;

        try
        {
            if (File.Exists(sourcePath))
            {
                string fileName = Path.GetFileName(sourcePath);
                string destPath = Path.Combine(targetDirectoryPath, fileName);
                if (!string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(sourcePath, destPath, true);
                    
                    // Move sidecar files (.tex or .msh if present)
                    string ext = Path.GetExtension(sourcePath).ToLower();
                    if (ext == ".ktx2")
                    {
                        string texSidecar = Path.ChangeExtension(sourcePath, ".tex");
                        if (File.Exists(texSidecar)) File.Move(texSidecar, Path.ChangeExtension(destPath, ".tex"), true);
                    }
                }
            }
            else if (Directory.Exists(sourcePath))
            {
                string dirName = Path.GetFileName(sourcePath);
                string destPath = Path.Combine(targetDirectoryPath, dirName);
                if (!string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase) && !destPath.StartsWith(sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(sourcePath, destPath);
                }
            }
        }
        catch (Exception ex)
        {
            Engine.CBindings.Log.Error($"[ContentBrowser] Failed to move '{sourcePath}': {ex.Message}", "ContentBrowser");
        }
    }

    public void Dispose()
    {
        _hoverPreviewTimer.Stop();
        _contentWatcher?.Dispose();
        _gameWatcher?.Dispose();
    }
}
