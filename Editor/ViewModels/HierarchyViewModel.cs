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

    public void SelectEntity(ulong id)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var ent in Entities)
            {
                if (ent.Id == id)
                {
                    SelectedEntity = ent;
                    break;
                }
            }
            if (id == 0)
            {
                SelectedEntity = null;
            }
        });
    }

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
            _world.OnEntityDeleted += HandleEntityDeleted;
            _world.OnWorldCleared += HandleWorldCleared;
            Refresh();
        }
    }

    private void HandleEntityCreated(ulong id)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Entities.Add(new HierarchyEntityViewModel(id, DescribeEntity(id)));
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

    private void HandleEntityDeleted(ulong id)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            for (int index = Entities.Count - 1;
                 index >= 0;
                 --index)
            {
                if (Entities[index].Id == id)
                    Entities.RemoveAt(index);
            }
            if (SelectedEntity?.Id == id)
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
                Entities.Add(new HierarchyEntityViewModel(ent, DescribeEntity(ent)));
            }
        }
    }

    private string DescribeEntity(ulong entityId)
    {
        if (_world == null) return $"Entity {entityId}";

        if (_world.TryGet<Engine.Scene.Components.Camera>(entityId, out _))
            return $"Camera {entityId}";
        if (_world.TryGet<Engine.RHI.ModelComponent>(entityId, out _))
            return $"Model {entityId}";
        if (_world.TryGet<Engine.RHI.PointLightComponent>(entityId, out _))
            return $"Point Light {entityId}";
        if (_world.TryGet<Engine.RHI.SpotLightComponent>(entityId, out _))
            return $"Spot Light {entityId}";
        if (_world.TryGet<Engine.RHI.DirectionalLightComponent>(entityId, out _))
            return $"Directional Light {entityId}";

        return $"Entity {entityId}";
    }

    private void UnbindWorld()
    {
        if (_world != null)
        {
            _world.OnEntityCreated -= HandleEntityCreated;
            _world.OnEntityDeleted -= HandleEntityDeleted;
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
