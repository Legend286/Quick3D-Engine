// SPDX-License-Identifier: MIT
using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Engine.Editor.Commands;

namespace Engine.Editor.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private EntitySnapshot? _gizmoEditStart;

    [ObservableProperty]
    private string _statusText = "Engine starting...";

    public string WindowTitle => (ViewportVm?.IsDirty == true ? "* " : "") + "Quick3D Engine Editor - " + (ViewportVm?.CurrentSceneName ?? "No Scene");

    public ConsolePanelViewModel ConsoleVm { get; } = new();
    public HierarchyViewModel HierarchyVm { get; } = new();
    public InspectorViewModel InspectorVm { get; } = new();
    public ContentBrowserViewModel ContentBrowserVm { get; } = new();
    public RenderGraphExplorerViewModel? RenderGraphVm { get; }

    /// <summary>Gets the active scene undo and redo history.</summary>
    public EditorCommandHistory Commands { get; } = new();

    /// <summary>Gets the editor-wide background asset import state.</summary>
    public Services.AssetImportService AssetImport { get; } =
        Services.AssetImportService.Shared;

    /// <summary>Bound to the central viewport panel. Owns the Metal swapchain
    /// + WriteableBitmap pipeline on macOS. Null on Windows until Phase 2
    /// Vulkan path lands.</summary>
    public ViewportPanelViewModel? ViewportVm { get; }

    public MainWindowViewModel()
    {
        if (OperatingSystem.IsMacOS())
        {
            string contentRoot = System.IO.Path.Combine(App.ProjectRoot, "Content");
            Engine.CBindings.Log.Info($"[MainWindowViewModel] ContentRoot: '{contentRoot}'", "Editor");
            ViewportVm = new ViewportPanelViewModel(contentRoot: contentRoot, sceneName: "New Scene");
            RenderGraphVm = new RenderGraphExplorerViewModel(ViewportVm);

            HierarchyVm.Bind(ViewportVm);
            HierarchyVm.OnEntitySelected += (ent) => {
                InspectorVm.SetSelectedEntity(ent);
                if (ent.HasValue) ViewportVm.GameLoop?.SetSelectedEntity(ent.Value);
                else ViewportVm.GameLoop?.SetSelectedEntity(0);
                ViewportVm.RequestRender();
            };

            ViewportVm.OnEntityPicked += (ent) => {
                HierarchyVm.SelectEntity(ent);
            };
            ViewportVm.OnEntityTransformEditStarted +=
                entity =>
                {
                    _gizmoEditStart =
                        ViewportVm.World == null
                            ? null
                            : EntitySnapshot.Capture(
                                ViewportVm.World,
                                entity);
                };
            ViewportVm.OnEntityTransformEditCompleted +=
                entity =>
                {
                    if (ViewportVm.World == null ||
                        _gizmoEditStart == null)
                    {
                        return;
                    }

                    EntitySnapshot? after =
                        EntitySnapshot.Capture(
                            ViewportVm.World,
                            entity);
                    if (after != null &&
                        after != _gizmoEditStart)
                    {
                        Commands.Record(
                            new EntityStateCommand(
                                "Transform Entity",
                                ViewportVm.World,
                                _gizmoEditStart,
                                after));
                    }
                    _gizmoEditStart = null;
                };

            ViewportVm.OnWorldCreated += () => InspectorVm.Bind(ViewportVm.World);
            InspectorVm.EntityEdited += (
                name,
                before,
                after) =>
            {
                if (ViewportVm.World == null)
                    return;
                Commands.Record(
                    new EntityStateCommand(
                        name,
                        ViewportVm.World,
                        before,
                        after));
                ViewportVm.MarkDirty();
            };
            ViewportVm.OnDirtyChanged += () => OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(WindowTitle)));
            ViewportVm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(ViewportPanelViewModel.CurrentSceneName))
                {
                    OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(WindowTitle)));
                }
            };
        }

        AssetImport.ImportCompleted +=
            () => ContentBrowserVm.RefreshCurrentFolder();
    }

    /// <summary>Records an entity that was created by an editor action.</summary>
    public void RecordCreatedEntity(
        ulong entityId,
        string commandName)
    {
        if (ViewportVm?.World == null)
            return;

        EntitySnapshot? snapshot =
            EntitySnapshot.Capture(
                ViewportVm.World,
                entityId);
        if (snapshot == null)
            return;

        Commands.Record(
            new EntityCreatedCommand(
                commandName,
                ViewportVm.World,
                snapshot));
        ViewportVm.MarkDirty();
    }

    /// <summary>Deletes the selected scene entity and records the mutation.</summary>
    public void DeleteSelectedEntity()
    {
        if (ViewportVm?.World == null ||
            HierarchyVm.SelectedEntity == null)
        {
            return;
        }

        ulong entityId = HierarchyVm.SelectedEntity.Id;
        EntitySnapshot? snapshot =
            EntitySnapshot.Capture(
                ViewportVm.World,
                entityId);
        if (snapshot == null ||
            !ViewportVm.World.DeleteEntity(entityId))
        {
            return;
        }

        Commands.Record(
            new EntityDeletedCommand(
                "Delete Entity",
                ViewportVm.World,
                snapshot));
        InspectorVm.SetSelectedEntity(null);
        ViewportVm.GameLoop?.SetSelectedEntity(0);
        ViewportVm.MarkDirty();
    }

    /// <summary>Undoes the latest scene mutation.</summary>
    public void Undo()
    {
        if (!Commands.CanUndo)
            return;
        Commands.Undo();
        ViewportVm?.MarkDirty();
    }

    /// <summary>Redoes the latest undone scene mutation.</summary>
    public void Redo()
    {
        if (!Commands.CanRedo)
            return;
        Commands.Redo();
        ViewportVm?.MarkDirty();
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
    }
}
