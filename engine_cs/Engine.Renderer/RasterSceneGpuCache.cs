// SPDX-License-Identifier: MIT

using System;
using System.Numerics;
using Engine.CBindings;
using Engine.RHI;
using Engine.Scene;

namespace Engine.Renderer;

internal sealed class RasterSceneGpuCache : IDisposable
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

    public RhiBuffer InstanceBuffer => _instanceBuffer[_bufferIndex];
    public RhiBuffer PartBuffer => _partBuffer[_bufferIndex];
    public RhiBuffer MaterialBuffer => _materialBuffer[_bufferIndex];
    public RhiBuffer CameraBuffer => _cameraBuffer[_bufferIndex];
    public RhiBuffer LightBuffer => _lightBuffer[_bufferIndex];
    public SceneFrameData FrameData { get; private set; } = new();
    public ScenePushData PushData { get; private set; }

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
        _preparedFrame = frameNumber;
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
