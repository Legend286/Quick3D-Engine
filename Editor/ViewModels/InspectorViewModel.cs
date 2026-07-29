// SPDX-License-Identifier: MIT
using System;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Engine.RHI;
using Engine.Scene;
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
        return LightMath.SanitizeQuaternion(q);
    }

    private void SyncLightDirectionFromTransform()
    {
        if (_world == null || !_selectedEntity.HasValue)
            return;

        if (!_world.TryGet<Transform>(_selectedEntity.Value, out var transform))
            return;

        if (_world.TryGet<SpotLightComponent>(_selectedEntity.Value, out var spotLight))
        {
            spotLight.Direction = LightMath.GetSpotDirection(transform.Rotation);
            _world.Set(_selectedEntity.Value, spotLight);
        }
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
            SyncLightDirectionFromTransform();
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

    private void UpdatePointLight()
    {
        if (_isUpdatingFromWorld || _world == null || !_selectedEntity.HasValue) return;

        if (_world.TryGet<PointLightComponent>(_selectedEntity.Value, out var light))
        {
            light.Color = new System.Numerics.Vector3(
                SanitizeFloat((float)PointLightColorR, 1f),
                SanitizeFloat((float)PointLightColorG, 1f),
                SanitizeFloat((float)PointLightColorB, 1f));
            light.Intensity = MathF.Max(SanitizeFloat((float)PointLightIntensity, 1f), 0f);
            light.Range = MathF.Max(SanitizeFloat((float)PointLightRange, 1f), 0.001f);
            light.SourceRadius = MathF.Max(SanitizeFloat((float)PointLightSourceRadius, 0f), 0f);
            light.CastShadows = PointLightCastShadows;
            _world.Set(_selectedEntity.Value, light);
        }
    }

    private void UpdateSpotLight()
    {
        if (_isUpdatingFromWorld || _world == null || !_selectedEntity.HasValue) return;

        if (_world.TryGet<SpotLightComponent>(_selectedEntity.Value, out var light))
        {
            var direction = new System.Numerics.Vector3(
                SanitizeFloat((float)SpotLightDirX, 0f),
                SanitizeFloat((float)SpotLightDirY, -1f),
                SanitizeFloat((float)SpotLightDirZ, 0f));
            direction = LightMath.NormalizeOrFallback(direction, LightMath.SpotLocalDirection);

            light.Color = new System.Numerics.Vector3(
                SanitizeFloat((float)SpotLightColorR, 1f),
                SanitizeFloat((float)SpotLightColorG, 1f),
                SanitizeFloat((float)SpotLightColorB, 1f));
            light.Intensity = MathF.Max(SanitizeFloat((float)SpotLightIntensity, 1f), 0f);
            light.Range = MathF.Max(SanitizeFloat((float)SpotLightRange, 1f), 0.001f);
            light.Direction = direction;
            light.InnerCone = Math.Clamp(SanitizeFloat((float)SpotLightInnerCone, 0.85f), 0.001f, 0.999f);
            float outerMax = Math.Max(light.InnerCone - 0.001f, 0.001f);
            light.OuterCone = Math.Clamp(SanitizeFloat((float)SpotLightOuterCone, 0.70f), 0.001f, outerMax);
            if (light.OuterCone >= light.InnerCone)
                light.OuterCone = Math.Max(light.InnerCone - 0.01f, 0.001f);
            light.SourceRadius = MathF.Max(SanitizeFloat((float)SpotLightSourceRadius, 0f), 0f);
            light.CastShadows = SpotLightCastShadows;
            _world.Set(_selectedEntity.Value, light);

            if (_world.TryGet<Transform>(_selectedEntity.Value, out var transform))
            {
                transform.Rotation = LightMath.GetSpotRotation(direction);
                transform.Rotation = SanitizeQuaternion(transform.Rotation);
                _lastSyncedRotation = transform.Rotation;
                _world.Set(_selectedEntity.Value, transform);
            }
        }
    }

    private void UpdateDirectionalLight()
    {
        if (_isUpdatingFromWorld || _world == null || !_selectedEntity.HasValue) return;

        if (_world.TryGet<DirectionalLightComponent>(_selectedEntity.Value, out var light))
        {
            var direction = new System.Numerics.Vector3(
                SanitizeFloat((float)DirectionalLightDirX, -0.4f),
                SanitizeFloat((float)DirectionalLightDirY, -1f),
                SanitizeFloat((float)DirectionalLightDirZ, -0.35f));
            direction = LightMath.NormalizeOrFallback(direction, new System.Numerics.Vector3(-0.4f, -1f, -0.35f));

            light.Color = new System.Numerics.Vector3(
                SanitizeFloat((float)DirectionalLightColorR, 1f),
                SanitizeFloat((float)DirectionalLightColorG, 1f),
                SanitizeFloat((float)DirectionalLightColorB, 1f));
            light.Intensity = MathF.Max(SanitizeFloat((float)DirectionalLightIntensity, 1f), 0f);
            light.Direction = direction;
            light.AngularRadius = MathF.Max(SanitizeFloat((float)DirectionalLightAngularRadius, 0.00465f), 0f);
            light.CastShadows = DirectionalLightCastShadows;
            _world.Set(_selectedEntity.Value, light);
        }
    }

    [ObservableProperty] private ulong _modelId;
    [ObservableProperty] private bool _staticShadowCaster = true;
    [ObservableProperty] private float _cameraFov;

    partial void OnStaticShadowCasterChanged(bool value)
    {
        if (_isUpdatingFromWorld ||
            _world == null ||
            !_selectedEntity.HasValue ||
            !_world.TryGet<ModelComponent>(
                _selectedEntity.Value,
                out var model))
        {
            return;
        }
        model.StaticShadowCaster = value;
        _world.Set(_selectedEntity.Value, model);
    }
    [ObservableProperty] private bool _hasDirectionalLight;
    [ObservableProperty] private bool _hasPointLight;
    [ObservableProperty] private bool _hasSpotLight;

    [ObservableProperty] private decimal _directionalLightColorR = 1m, _directionalLightColorG = 1m, _directionalLightColorB = 1m;
    [ObservableProperty] private decimal _directionalLightIntensity = 3.5m;
    [ObservableProperty] private decimal _directionalLightDirX = -0.4m, _directionalLightDirY = -1.0m, _directionalLightDirZ = -0.35m;
    [ObservableProperty] private decimal _directionalLightAngularRadius = 0.012m;
    [ObservableProperty] private bool _directionalLightCastShadows = true;

    [ObservableProperty] private decimal _pointLightColorR = 1m, _pointLightColorG = 1m, _pointLightColorB = 1m;
    [ObservableProperty] private decimal _pointLightIntensity = 20m;
    [ObservableProperty] private decimal _pointLightRange = 10m;
    [ObservableProperty] private decimal _pointLightSourceRadius = 0.05m;
    [ObservableProperty] private bool _pointLightCastShadows = true;

    [ObservableProperty] private decimal _spotLightColorR = 1m, _spotLightColorG = 1m, _spotLightColorB = 1m;
    [ObservableProperty] private decimal _spotLightIntensity = 30m;
    [ObservableProperty] private decimal _spotLightRange = 12m;
    [ObservableProperty] private decimal _spotLightDirX = 0m, _spotLightDirY = -1m, _spotLightDirZ = 0m;
    [ObservableProperty] private decimal _spotLightInnerCone = 0.85m;
    [ObservableProperty] private decimal _spotLightOuterCone = 0.70m;
    [ObservableProperty] private decimal _spotLightSourceRadius = 0.03m;
    [ObservableProperty] private bool _spotLightCastShadows = true;

    partial void OnDirectionalLightColorRChanged(decimal value) => UpdateDirectionalLight();
    partial void OnDirectionalLightColorGChanged(decimal value) => UpdateDirectionalLight();
    partial void OnDirectionalLightColorBChanged(decimal value) => UpdateDirectionalLight();
    partial void OnDirectionalLightIntensityChanged(decimal value) => UpdateDirectionalLight();
    partial void OnDirectionalLightDirXChanged(decimal value) => UpdateDirectionalLight();
    partial void OnDirectionalLightDirYChanged(decimal value) => UpdateDirectionalLight();
    partial void OnDirectionalLightDirZChanged(decimal value) => UpdateDirectionalLight();
    partial void OnDirectionalLightAngularRadiusChanged(decimal value) => UpdateDirectionalLight();
    partial void OnDirectionalLightCastShadowsChanged(bool value) => UpdateDirectionalLight();

    partial void OnPointLightColorRChanged(decimal value) => UpdatePointLight();
    partial void OnPointLightColorGChanged(decimal value) => UpdatePointLight();
    partial void OnPointLightColorBChanged(decimal value) => UpdatePointLight();
    partial void OnPointLightIntensityChanged(decimal value) => UpdatePointLight();
    partial void OnPointLightRangeChanged(decimal value) => UpdatePointLight();
    partial void OnPointLightSourceRadiusChanged(decimal value) => UpdatePointLight();
    partial void OnPointLightCastShadowsChanged(bool value) => UpdatePointLight();

    partial void OnSpotLightColorRChanged(decimal value) => UpdateSpotLight();
    partial void OnSpotLightColorGChanged(decimal value) => UpdateSpotLight();
    partial void OnSpotLightColorBChanged(decimal value) => UpdateSpotLight();
    partial void OnSpotLightIntensityChanged(decimal value) => UpdateSpotLight();
    partial void OnSpotLightRangeChanged(decimal value) => UpdateSpotLight();
    partial void OnSpotLightDirXChanged(decimal value) => UpdateSpotLight();
    partial void OnSpotLightDirYChanged(decimal value) => UpdateSpotLight();
    partial void OnSpotLightDirZChanged(decimal value) => UpdateSpotLight();
    partial void OnSpotLightInnerConeChanged(decimal value) => UpdateSpotLight();
    partial void OnSpotLightOuterConeChanged(decimal value) => UpdateSpotLight();
    partial void OnSpotLightSourceRadiusChanged(decimal value) => UpdateSpotLight();
    partial void OnSpotLightCastShadowsChanged(bool value) => UpdateSpotLight();

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
            HasDirectionalLight = false;
            HasPointLight = false;
            HasSpotLight = false;
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
            _isUpdatingFromWorld = true;
            HasModel = true;
            ModelId = model.ModelId;
            StaticShadowCaster = model.StaticShadowCaster;
            _isUpdatingFromWorld = false;
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

        if (_world.TryGet<DirectionalLightComponent>(ent, out var directionalLight))
        {
            _isUpdatingFromWorld = true;
            HasDirectionalLight = true;
            DirectionalLightColorR = (decimal)SanitizeFloat(directionalLight.Color.X, 1f);
            DirectionalLightColorG = (decimal)SanitizeFloat(directionalLight.Color.Y, 1f);
            DirectionalLightColorB = (decimal)SanitizeFloat(directionalLight.Color.Z, 1f);
            DirectionalLightIntensity = (decimal)SanitizeFloat(directionalLight.Intensity, 1f);
            DirectionalLightDirX = (decimal)SanitizeFloat(directionalLight.Direction.X, -0.4f);
            DirectionalLightDirY = (decimal)SanitizeFloat(directionalLight.Direction.Y, -1f);
            DirectionalLightDirZ = (decimal)SanitizeFloat(directionalLight.Direction.Z, -0.35f);
            DirectionalLightAngularRadius = (decimal)SanitizeFloat(directionalLight.AngularRadius, 0.00465f);
            DirectionalLightCastShadows = directionalLight.CastShadows;
            _isUpdatingFromWorld = false;
        }
        else
        {
            HasDirectionalLight = false;
        }

        if (_world.TryGet<PointLightComponent>(ent, out var pointLight))
        {
            _isUpdatingFromWorld = true;
            HasPointLight = true;
            PointLightColorR = (decimal)SanitizeFloat(pointLight.Color.X, 1f);
            PointLightColorG = (decimal)SanitizeFloat(pointLight.Color.Y, 1f);
            PointLightColorB = (decimal)SanitizeFloat(pointLight.Color.Z, 1f);
            PointLightIntensity = (decimal)SanitizeFloat(pointLight.Intensity, 1f);
            PointLightRange = (decimal)SanitizeFloat(pointLight.Range, 1f);
            PointLightSourceRadius = (decimal)SanitizeFloat(pointLight.SourceRadius, 0f);
            PointLightCastShadows = pointLight.CastShadows;
            _isUpdatingFromWorld = false;
        }
        else
        {
            HasPointLight = false;
        }

        if (_world.TryGet<SpotLightComponent>(ent, out var spotLight))
        {
            _isUpdatingFromWorld = true;
            HasSpotLight = true;
            if (_world.TryGet<Transform>(ent, out var lightTransform))
                spotLight.Direction = LightMath.GetSpotDirection(lightTransform.Rotation);
            SpotLightColorR = (decimal)SanitizeFloat(spotLight.Color.X, 1f);
            SpotLightColorG = (decimal)SanitizeFloat(spotLight.Color.Y, 1f);
            SpotLightColorB = (decimal)SanitizeFloat(spotLight.Color.Z, 1f);
            SpotLightIntensity = (decimal)SanitizeFloat(spotLight.Intensity, 1f);
            SpotLightRange = (decimal)SanitizeFloat(spotLight.Range, 1f);
            SpotLightDirX = (decimal)SanitizeFloat(spotLight.Direction.X, 0f);
            SpotLightDirY = (decimal)SanitizeFloat(spotLight.Direction.Y, -1f);
            SpotLightDirZ = (decimal)SanitizeFloat(spotLight.Direction.Z, 0f);
            SpotLightInnerCone = (decimal)SanitizeFloat(spotLight.InnerCone, 0.85f);
            SpotLightOuterCone = (decimal)SanitizeFloat(spotLight.OuterCone, 0.70f);
            SpotLightSourceRadius = (decimal)SanitizeFloat(spotLight.SourceRadius, 0f);
            SpotLightCastShadows = spotLight.CastShadows;
            _isUpdatingFromWorld = false;
        }
        else
        {
            HasSpotLight = false;
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
