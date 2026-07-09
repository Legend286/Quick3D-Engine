// SPDX-License-Identifier: MIT
using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Engine.RHI;

namespace Engine.Editor.ViewModels;

public partial class HierarchyEntityViewModel : ObservableObject
{
    public ulong Id { get; }

    [ObservableProperty]
    private string _name;

    public HierarchyEntityViewModel(ulong id, string name)
    {
        Id = id;
        _name = name;
    }
}

public partial class HierarchyViewModel : ObservableObject, IDisposable
{
    private EcsWorld? _world;
    private ViewportPanelViewModel? _viewport;

    public ObservableCollection<HierarchyEntityViewModel> Entities { get; } = new();

    [ObservableProperty]
    private HierarchyEntityViewModel? _selectedEntity;

    partial void OnSelectedEntityChanged(HierarchyEntityViewModel? value)
    {
        OnEntitySelected?.Invoke(value?.Id);
    }

    public event Action<ulong?>? OnEntitySelected;

    public void Bind(ViewportPanelViewModel viewport)
    {
        Unbind();
        _viewport = viewport;
        _viewport.OnWorldCreated += HandleWorldCreated;
        HandleWorldCreated();
    }

    private void HandleWorldCreated()
    {
        UnbindWorld();
        _world = _viewport?.World;
        if (_world != null)
        {
            _world.OnEntityCreated += HandleEntityCreated;
            _world.OnWorldCleared += HandleWorldCleared;
            Refresh();
        }
    }

    private void HandleEntityCreated(ulong id)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Entities.Add(new HierarchyEntityViewModel(id, $"Entity {id}"));
        });
    }

    private void HandleWorldCleared()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Entities.Clear();
            SelectedEntity = null;
        });
    }

    private void Refresh()
    {
        Entities.Clear();
        SelectedEntity = null;
        if (_world != null)
        {
            foreach (var ent in _world.Entities)
            {
                Entities.Add(new HierarchyEntityViewModel(ent, $"Entity {ent}"));
            }
        }
    }

    private void UnbindWorld()
    {
        if (_world != null)
        {
            _world.OnEntityCreated -= HandleEntityCreated;
            _world.OnWorldCleared -= HandleWorldCleared;
        }
    }

    public void Unbind()
    {
        if (_viewport != null)
        {
            _viewport.OnWorldCreated -= HandleWorldCreated;
        }
        UnbindWorld();
    }

    public void Dispose()
    {
        Unbind();
    }
}
