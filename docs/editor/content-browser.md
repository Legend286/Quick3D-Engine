# Content Browser

The Content Browser is a core editor panel responsible for displaying and managing assets within the project's content directory.

## Features
- **Asset Discovery**: Scans the `Content/` directory for cookable and runtime assets (`.scene.json`, `.mat`, `.png`, `.mdl`, etc.).
- **Hierarchical View**: Displays a tree view of directories on the left panel, and a grid view of files on the right.
- **Drag and Drop**: Supports dragging assets from the content browser directly into the viewport (e.g. for spawning meshes or applying materials).
- **Auto-Refresh**: Watches the filesystem for changes to keep the view in sync with the actual files on disk.

## Implementation Details
The content browser is built using Avalonia UI (`ContentBrowserView.axaml` and `ContentBrowserViewModel.cs`). It uses standard .NET file system APIs (`System.IO.FileSystemWatcher` and `DirectoryInfo`) to enumerate and track file changes.

It is registered as a dockable panel in the main editor window layout.
