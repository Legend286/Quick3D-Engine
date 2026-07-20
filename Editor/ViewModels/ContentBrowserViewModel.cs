// SPDX-License-Identifier: MIT
using System;
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

    public ContentAsset(string name, string fullPath, string assetType, string iconGlyph)
    {
        Name = name;
        FullPath = fullPath;
        AssetType = assetType;
        IconGlyph = iconGlyph;
    }
}

public partial class ContentBrowserViewModel : ObservableObject, IDisposable
{
    [ObservableProperty] private ObservableCollection<ContentFolder> _rootFolders = new();
    [ObservableProperty] private ContentFolder? _selectedFolder;
    [ObservableProperty] private ObservableCollection<ContentAsset> _currentAssets = new();

    private FileSystemWatcher? _watcher;

    public ContentBrowserViewModel()
    {
        InitializeFolders();
        SetupWatcher();
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
        LoadAssetsForFolder(newValue);
    }

    private void LoadAssetsForFolder(ContentFolder? folder)
    {
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
                        icon = "\uE8B8"; // settings/world icon
                        break;
                    default:
                        continue; // Skip unrecognized
                }

                var asset = new ContentAsset(Path.GetFileName(file), file, type, icon);
                CurrentAssets.Add(asset);

                // Queue thumbnail loading/generation
                if (type == "Model" || type == "Material" || type == "Texture")
                {
                    Task.Run(async () =>
                    {
                        var bmp = await Services.ThumbnailGenerator.GetOrGenerateThumbnailAsync(file, type);
                        if (bmp != null)
                        {
                            Dispatcher.UIThread.Post(() => asset.ThumbnailBitmap = bmp);
                        }
                    });
                }
            }
        }
        catch { }
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

    public void Dispose()
    {
        _contentWatcher?.Dispose();
        _gameWatcher?.Dispose();
    }
}
