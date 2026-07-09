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

    private void SetupWatcher()
    {
        var rootDir = Path.GetFullPath(".");
        _watcher = new FileSystemWatcher(rootDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
        };

        _watcher.Created += OnFileSystemChanged;
        _watcher.Deleted += OnFileSystemChanged;
        _watcher.Renamed += OnFileSystemChanged;

        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var oldSelectedPath = SelectedFolder?.FullPath;
            InitializeFolders();

            if (oldSelectedPath != null)
            {
                var folder = FindFolderByPath(RootFolders, oldSelectedPath);
                if (folder != null)
                    SelectedFolder = folder;
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
        _watcher?.Dispose();
    }
}
