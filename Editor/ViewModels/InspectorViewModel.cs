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
    private bool _isUpdatingFromWorld;
    private System.Numerics.Quaternion _lastSyncedRotation = System.Numerics.Quaternion.Identity;

    [ObservableProperty]
    private bool _hasSelection;

    [ObservableProperty]
    private string _entityName = "No Selection";

    // Component states
    [ObservableProperty] private bool _hasTransform;
    [ObservableProperty] private bool _hasModel;
    [ObservableProperty] private bool _hasCamera;

    [ObservableProperty] private decimal _posX, _posY, _posZ;
    [ObservableProperty] private decimal _rotX, _rotY, _rotZ;
    [ObservableProperty] private decimal _scaleX = 1m, _scaleY = 1m, _scaleZ = 1m;

    private bool _isEditingRotation;

    partial void OnPosXChanged(decimal value) => UpdateWorldTransform();
    partial void OnPosYChanged(decimal value) => UpdateWorldTransform();
    partial void OnPosZChanged(decimal value) => UpdateWorldTransform();
    
    partial void OnRotXChanged(decimal value) => UpdateWorldRotation();
    partial void OnRotYChanged(decimal value) => UpdateWorldRotation();
    partial void OnRotZChanged(decimal value) => UpdateWorldRotation();

    private static float SanitizeFloat(float v, float fallback = 0f)
    {
        return float.IsNaN(v) || float.IsInfinity(v) ? fallback : v;
    }

    private static float SanitizeScale(float v)
    {
        if (float.IsNaN(v) || float.IsInfinity(v) || MathF.Abs(v) < 1e-5f) return 1f;
        return v;
    }

    private static System.Numerics.Quaternion SanitizeQuaternion(System.Numerics.Quaternion q)
    {
        if (float.IsNaN(q.X) || float.IsNaN(q.Y) || float.IsNaN(q.Z) || float.IsNaN(q.W) ||
            float.IsInfinity(q.X) || float.IsInfinity(q.Y) || float.IsInfinity(q.Z) || float.IsInfinity(q.W) ||
            q.LengthSquared() < 1e-6f)
        {
            return System.Numerics.Quaternion.Identity;
        }
        return System.Numerics.Quaternion.Normalize(q);
    }

    private void UpdateWorldRotation()
    {
        if (_isUpdatingFromWorld || _isEditingRotation || _world == null || !_selectedEntity.HasValue) return;
        if (_world.TryGet<Transform>(_selectedEntity.Value, out var t))
        {
            float rx = SanitizeFloat((float)RotX);
            float ry = SanitizeFloat((float)RotY);
            float rz = SanitizeFloat((float)RotZ);

            var q = System.Numerics.Quaternion.CreateFromYawPitchRoll(
                ry * (MathF.PI / 180f),
                rx * (MathF.PI / 180f),
                rz * (MathF.PI / 180f));
            
            t.Rotation = SanitizeQuaternion(q);
            _lastSyncedRotation = t.Rotation;
            _world.Set(_selectedEntity.Value, t);
        }
    }

    partial void OnScaleXChanged(decimal value) => UpdateWorldTransform();
    partial void OnScaleYChanged(decimal value) => UpdateWorldTransform();
    partial void OnScaleZChanged(decimal value) => UpdateWorldTransform();

    private void UpdateWorldTransform()
    {
        if (_isUpdatingFromWorld || _world == null || !_selectedEntity.HasValue) return;

        if (_world.TryGet<Transform>(_selectedEntity.Value, out var t))
        {
            t.Position = new System.Numerics.Vector3(
                SanitizeFloat((float)PosX),
                SanitizeFloat((float)PosY),
                SanitizeFloat((float)PosZ));

            t.Scale = new System.Numerics.Vector3(
                SanitizeScale((float)ScaleX),
                SanitizeScale((float)ScaleY),
                SanitizeScale((float)ScaleZ));

            t.Rotation = SanitizeQuaternion(t.Rotation);
            _world.Set(_selectedEntity.Value, t);
        }
    }

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
            _isUpdatingFromWorld = true;

            HasTransform = true;
            PosX = (decimal)SanitizeFloat(transform.Position.X);
            PosY = (decimal)SanitizeFloat(transform.Position.Y);
            PosZ = (decimal)SanitizeFloat(transform.Position.Z);

            var rot = SanitizeQuaternion(transform.Rotation);
            float dot = System.Numerics.Quaternion.Dot(rot, _lastSyncedRotation);
            if (MathF.Abs(dot) < 0.9999f)
            {
                var euler = ToEulerAngles(rot);
                _isEditingRotation = true;
                RotX = (decimal)SanitizeFloat(euler.X);
                RotY = (decimal)SanitizeFloat(euler.Y);
                RotZ = (decimal)SanitizeFloat(euler.Z);
                _isEditingRotation = false;
                _lastSyncedRotation = rot;
            }
            else
            {
                _lastSyncedRotation = rot;
            }

            ScaleX = (decimal)SanitizeScale(transform.Scale.X);
            ScaleY = (decimal)SanitizeScale(transform.Scale.Y);
            ScaleZ = (decimal)SanitizeScale(transform.Scale.Z);
            
            _isUpdatingFromWorld = false;
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
        q = SanitizeQuaternion(q);
        float sinPitch = 2 * (q.W * q.X - q.Y * q.Z);
        float pitch = float.IsNaN(sinPitch) ? 0f : MathF.Asin(MathF.Max(-1f, MathF.Min(1f, sinPitch)));
        float yaw = MathF.Atan2(2 * (q.W * q.Y + q.Z * q.X), 1 - 2 * (q.X * q.X + q.Y * q.Y));
        float roll = MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.X * q.X + q.Z * q.Z));
        
        return new System.Numerics.Vector3(
            SanitizeFloat(pitch * (180f / MathF.PI)),
            SanitizeFloat(yaw * (180f / MathF.PI)),
            SanitizeFloat(roll * (180f / MathF.PI)));
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
