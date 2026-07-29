# Content Browser

The Content Browser is a core editor panel responsible for displaying and managing assets within the project's content directory.

## Features
- **Asset Discovery**: Scans the `Content/` directory for cookable and runtime assets (`.scene.json`, `.mat`, `.png`, `.mdl`, etc.).
- **Hierarchical View**: Displays a tree view of directories on the left panel, and a grid view of files on the right.
- **Drag and Drop Spawning & Material Assignment**:
  - Dragging `.mdl` or `.scene.json` into the Viewport spawns the model entity into the active scene.
  - Dragging `.mat` materials onto a model in the Viewport queries the hardware ID picking pass to identify the exact target entity and hit triangle/submesh material index below the mouse cursor, assigning the material directly to that submesh.
  - Dragging textures (`.ktx2`, `.png`, `.jpg`, etc.) into the Material Editor slots assigns active textures to base or layer slots.
- **Folder and File Relocation**: Dragging files or subfolders into directory tree nodes moves items on disk and updates paths.
- **Auto-Refresh**: Watches the filesystem for changes to keep the view in sync with the actual files on disk.

## Implementation Details
The content browser is built using Avalonia UI (`ContentBrowserView.axaml` and `ContentBrowserViewModel.cs`). It uses standard .NET file system APIs (`System.IO.FileSystemWatcher` and `DirectoryInfo`) to enumerate and track file changes.

Asset hover previews are owned by the asset tile currently under the pointer.
Leaving that tile closes the preview and releases its preview-scene resources.
Moving directly between tiles transfers ownership without allowing a delayed exit
event from the previous tile to close the newly requested preview.

The popup keeps one external RHI render target and synchronization primitive
for its lifetime. Model and material changes rebuild only the preview scene;
they do not repeatedly export and import GPU resources through the compositor.

Offscreen thumbnail workers initialize `IGameLoop` with ImGui disabled.
Dear ImGui owns process-global font-atlas state that cannot be built safely
from a thumbnail thread while the interactive viewport is rendering.

It is registered as a dockable panel in the main editor window layout.
