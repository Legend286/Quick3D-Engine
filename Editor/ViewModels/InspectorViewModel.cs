// SPDX-License-Identifier: MIT
using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Engine.RHI;
using Engine.Scene.Components;

namespace Engine.Editor.ViewModels;

public partial class InspectorViewModel : ObservableObject, IDisposable
{
    private EcsWorld? _world;
    private ulong? _selectedEntity;
    private Avalonia.Threading.DispatcherTimer _timer;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private string _entityName = "No Selection";

    // Component states
    [ObservableProperty] private bool _hasTransform;
    [ObservableProperty] private bool _hasModel;
    [ObservableProperty] private bool _hasCamera;

    [ObservableProperty] private float _posX, _posY, _posZ;
    [ObservableProperty] private float _rotX, _rotY, _rotZ;
    [ObservableProperty] private float _scaleX = 1f, _scaleY = 1f, _scaleZ = 1f;

    [ObservableProperty] private ulong _modelId;
    [ObservableProperty] private float _cameraFov;

    public InspectorViewModel()
    {
        _timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void Bind(EcsWorld? world)
    {
        _world = world;
        Refresh();
    }

    public void SetSelectedEntity(ulong? entityId)
    {
        _selectedEntity = entityId;
        HasSelection = entityId.HasValue;
        EntityName = entityId.HasValue ? $"Entity {entityId.Value}" : "No Selection";
        Refresh();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_world != null && _selectedEntity.HasValue)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        if (_world == null || !_selectedEntity.HasValue)
        {
            HasTransform = false;
            HasModel = false;
            HasCamera = false;
            return;
        }

        ulong ent = _selectedEntity.Value;

        if (_world.TryGet<Transform>(ent, out var transform))
        {
            HasTransform = true;
            PosX = transform.Position.X;
            PosY = transform.Position.Y;
            PosZ = transform.Position.Z;

            // Simplified rotation extraction for read-only display
            var euler = ToEulerAngles(transform.Rotation);
            RotX = euler.X;
            RotY = euler.Y;
            RotZ = euler.Z;

            ScaleX = transform.Scale.X;
            ScaleY = transform.Scale.Y;
            ScaleZ = transform.Scale.Z;
        }
        else
        {
            HasTransform = false;
        }

        if (_world.TryGet<Engine.RHI.ModelComponent>(ent, out var model))
        {
            HasModel = true;
            ModelId = model.ModelId;
        }
        else
        {
            HasModel = false;
        }

        if (_world.TryGet<Engine.Scene.Components.Camera>(ent, out var camera))
        {
            HasCamera = true;
            CameraFov = camera.FieldOfView * (180f / MathF.PI); // Convert to degrees for display
        }
        else
        {
            HasCamera = false;
        }
    }

    private System.Numerics.Vector3 ToEulerAngles(System.Numerics.Quaternion q)
    {
        // Approximation for UI display
        float sinr_cosp = 2 * (q.W * q.X + q.Y * q.Z);
        float cosr_cosp = 1 - 2 * (q.X * q.X + q.Y * q.Y);
        float roll = MathF.Atan2(sinr_cosp, cosr_cosp);

        float sinp = MathF.Sqrt(1 + 2 * (q.W * q.Y - q.X * q.Z));
        float cosp = MathF.Sqrt(1 - 2 * (q.W * q.Y - q.X * q.Z));
        float pitch = 2 * MathF.Atan2(sinp, cosp) - MathF.PI / 2;

        float siny_cosp = 2 * (q.W * q.Z + q.X * q.Y);
        float cosy_cosp = 1 - 2 * (q.Y * q.Y + q.Z * q.Z);
        float yaw = MathF.Atan2(siny_cosp, cosy_cosp);

        return new System.Numerics.Vector3(roll, pitch, yaw) * (180f / MathF.PI);
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
