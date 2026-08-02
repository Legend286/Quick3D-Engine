// SPDX-License-Identifier: MIT

using System;
using System.Numerics;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.Scene;

namespace Engine.Renderer;

internal sealed class RasterSceneGpuCache : IDisposable, ISceneGpuDataProvider
{
    private readonly RhiDevice _device;
    private readonly IEntityStore _world;
    private readonly SceneGraph _scene;
    private readonly RhiBindlessHeap _bindlessHeap;
    private readonly Renderer _renderer;

    private long _preparedFrame = -1;
    private RhiBuffer[] _instanceBuffer = new RhiBuffer[3];
    private RhiBuffer[] _partBuffer = new RhiBuffer[3];
    private RhiBuffer[] _materialBuffer = new RhiBuffer[3];
    private RhiBuffer[] _cameraBuffer = new RhiBuffer[3];
    private RhiBuffer[] _lightBuffer = new RhiBuffer[3];
    private int _bufferIndex = 0;
    private int _lightHash;
    private uint _lightRevision;
    private int _skyHash;
    private uint _skyRevision;
    private int _geometryHash;
    private uint _geometryRevision;
    private bool _hasSceneBounds;
    private Vector3 _sceneBoundsMin;
    private Vector3 _sceneBoundsMax;

    public RhiBuffer InstanceBuffer => _instanceBuffer[_bufferIndex];
    public RhiBuffer PartBuffer => _partBuffer[_bufferIndex];
    public RhiBuffer MaterialBuffer => _materialBuffer[_bufferIndex];
    public RhiBuffer CameraBuffer => _cameraBuffer[_bufferIndex];
    public RhiBuffer LightBuffer => _lightBuffer[_bufferIndex];
    public SceneFrameData FrameData { get; private set; } = new();
    public ScenePushData PushData { get; private set; }

    RhiBuffer ISceneGpuDataProvider.CurrentLightBuffer => LightBuffer;
    uint ISceneGpuDataProvider.CurrentLightCount =>
        (uint)FrameData.Lights.Count;
    uint ISceneGpuDataProvider.CurrentLightRevision =>
        _lightRevision;
    Vector4 ISceneGpuDataProvider.CurrentSkySunDirectionAndRadius =>
        PushData.Sky.SunDirAndRadius;
    Vector4 ISceneGpuDataProvider.CurrentSkyAtmosphereParameters =>
        PushData.Sky.IntensityTurbidityAlbedoPad;
    uint ISceneGpuDataProvider.CurrentSkyRevision =>
        _skyRevision;
    uint ISceneGpuDataProvider.CurrentGeometryRevision =>
        _geometryRevision;
    RhiBuffer ISceneGpuDataProvider.CurrentInstanceBuffer =>
        InstanceBuffer;
    RhiBuffer ISceneGpuDataProvider.CurrentPartBuffer =>
        PartBuffer;
    RhiBuffer ISceneGpuDataProvider.CurrentMaterialBuffer =>
        MaterialBuffer;
    uint ISceneGpuDataProvider.CurrentInstanceCount =>
        (uint)FrameData.Instances.Count;
    uint ISceneGpuDataProvider.CurrentPartCount =>
        (uint)FrameData.Parts.Count;
    uint ISceneGpuDataProvider.CurrentMaterialCount =>
        (uint)FrameData.Materials.Count;

    bool ISceneGpuDataProvider.TryGetSceneBounds(
        out Vector3 minimum,
        out Vector3 maximum)
    {
        minimum = _sceneBoundsMin;
        maximum = _sceneBoundsMax;
        return _hasSceneBounds;
    }

    void ISceneGpuDataProvider.PrepareSceneGpuData(
        long frameNumber,
        uint width,
        uint height)
    {
        uint safeHeight = Math.Max(height, 1u);
        Prepare(
            frameNumber,
            (float)Math.Max(width, 1u) / safeHeight,
            width,
            height);
    }

    public unsafe RasterSceneGpuCache(
        RhiDevice device,
        IEntityStore world,
        SceneGraph scene,
        RhiBindlessHeap bindlessHeap,
        Renderer renderer)
    {
        _device = device;
        _world = world;
        _scene = scene;
        _bindlessHeap = bindlessHeap;
        _renderer = renderer;

        for (int i = 0; i < 3; i++)
        {
            _instanceBuffer[i] = RhiBuffer.Create(
                device, 1024 * (ulong)sizeof(InstanceData), RhiNative.BufferUsage.Storage);
            _partBuffer[i] = RhiBuffer.Create(
                device, 4096 * (ulong)sizeof(PartData), RhiNative.BufferUsage.Storage);
            _materialBuffer[i] = RhiBuffer.Create(
                device, 1024 * (ulong)sizeof(MaterialData), RhiNative.BufferUsage.Storage);
            _cameraBuffer[i] = RhiBuffer.Create(
                device, (ulong)sizeof(CameraData), RhiNative.BufferUsage.Storage);
            _lightBuffer[i] = RhiBuffer.Create(
                device, 1024 * (ulong)sizeof(LightData), RhiNative.BufferUsage.Storage);
        }
    }

    public void Prepare(long frameNumber, float aspect, uint width, uint height)
    {
        if (_preparedFrame == frameNumber)
            return;

        _bufferIndex = (int)(frameNumber % 3);

        SceneDataExtractor.Extract(
            _device,
            _world,
            _scene,
            _bindlessHeap,
            aspect,
            ref _cameraBuffer[_bufferIndex],
            ref _lightBuffer[_bufferIndex],
            ref _instanceBuffer[_bufferIndex],
            ref _partBuffer[_bufferIndex],
            ref _materialBuffer[_bufferIndex],
            _renderer.AnimationFrameContext,
            _renderer.ActiveCameraEntity,
            Vector3.UnitZ,
            unchecked((uint)frameNumber),
            _renderer.DebugFlags,
            _renderer.ProjectionBlend,
            _renderer.OrthographicSize,
            width,
            height,
            out SceneFrameData frameData,
            out ScenePushData pushData);

        FrameData = frameData;
        PushData = pushData;
        int lightHash = 17;
        foreach (LightData light in frameData.Lights)
        {
            lightHash = HashCode.Combine(
                lightHash,
                light.Position,
                light.Direction,
                light.Color,
                light.ShapeParams);
        }
        if (lightHash != _lightHash)
        {
            _lightHash = lightHash;
            _lightRevision++;
            if (_lightRevision == 0)
                _lightRevision = 1;
        }
        int skyHash = HashCode.Combine(
            pushData.Sky.SunDirAndRadius,
            pushData.Sky.IntensityTurbidityAlbedoPad);
        if (skyHash != _skyHash)
        {
            _skyHash = skyHash;
            _skyRevision++;
            if (_skyRevision == 0)
                _skyRevision = 1;
        }
        UpdateGeometryState(frameData);
        _preparedFrame = frameNumber;
    }

    private void UpdateGeometryState(SceneFrameData frameData)
    {
        int geometryHash = 17;
        Vector3 boundsMin = new(float.MaxValue);
        Vector3 boundsMax = new(float.MinValue);
        foreach (InstanceData instance in frameData.Instances)
        {
            geometryHash = HashCode.Combine(
                geometryHash,
                instance.ModelMatrix,
                instance.AabbMin,
                instance.AabbMax,
                instance.PartCount,
                instance.FirstPartIndex,
                instance.EntityIdLow,
                instance.EntityIdHigh);
            Vector3 localMin = new(
                instance.AabbMin.X,
                instance.AabbMin.Y,
                instance.AabbMin.Z);
            Vector3 localMax = new(
                instance.AabbMax.X,
                instance.AabbMax.Y,
                instance.AabbMax.Z);
            for (int corner = 0; corner < 8; ++corner)
            {
                Vector3 local = new(
                    (corner & 1) == 0 ? localMin.X : localMax.X,
                    (corner & 2) == 0 ? localMin.Y : localMax.Y,
                    (corner & 4) == 0 ? localMin.Z : localMax.Z);
                Vector3 world = Vector3.Transform(
                    local,
                    instance.ModelMatrix);
                boundsMin = Vector3.Min(boundsMin, world);
                boundsMax = Vector3.Max(boundsMax, world);
            }
        }
        foreach (PartData part in frameData.Parts)
        {
            geometryHash = HashCode.Combine(
                geometryHash,
                part.AabbMin,
                part.AabbMax,
                part.LocalOffset,
                part.Vertices,
                part.Indices,
                part.IndexCount,
                part.Flags);
        }
        _hasSceneBounds = frameData.Instances.Count > 0;
        _sceneBoundsMin = _hasSceneBounds ? boundsMin : Vector3.Zero;
        _sceneBoundsMax = _hasSceneBounds ? boundsMax : Vector3.Zero;
        if (geometryHash == _geometryHash)
            return;
        _geometryHash = geometryHash;
        ++_geometryRevision;
        if (_geometryRevision == 0)
            _geometryRevision = 1;
    }

    public void Dispose()
    {
        for (int i = 0; i < 3; i++)
        {
            _instanceBuffer[i].Dispose();
            _partBuffer[i].Dispose();
            _materialBuffer[i].Dispose();
            _cameraBuffer[i].Dispose();
            _lightBuffer[i].Dispose();
        }
    }
}
