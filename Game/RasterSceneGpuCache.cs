// SPDX-License-Identifier: MIT

using System;
using System.Numerics;
using Engine.CBindings;
using Engine.RHI;
using Engine.Scene;

namespace Engine.Game;

internal sealed class RasterSceneGpuCache : IDisposable
{
    private readonly RhiDevice _device;
    private readonly IEntityStore _world;
    private readonly SceneGraph _scene;
    private readonly RhiBindlessHeap _bindlessHeap;
    private readonly Renderer _renderer;

    private long _preparedFrame = -1;
    private RhiBuffer _instanceBuffer;
    private RhiBuffer _partBuffer;
    private RhiBuffer _materialBuffer;
    private RhiBuffer _cameraBuffer;
    private RhiBuffer _lightBuffer;

    public RhiBuffer InstanceBuffer => _instanceBuffer;
    public RhiBuffer PartBuffer => _partBuffer;
    public RhiBuffer MaterialBuffer => _materialBuffer;
    public RhiBuffer CameraBuffer => _cameraBuffer;
    public RhiBuffer LightBuffer => _lightBuffer;
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

        _instanceBuffer = RhiBuffer.Create(
            device, 1024 * (ulong)sizeof(InstanceData), RhiNative.BufferUsage.Storage);
        _partBuffer = RhiBuffer.Create(
            device, 4096 * (ulong)sizeof(PartData), RhiNative.BufferUsage.Storage);
        _materialBuffer = RhiBuffer.Create(
            device, 1024 * (ulong)sizeof(MaterialData), RhiNative.BufferUsage.Storage);
        _cameraBuffer = RhiBuffer.Create(
            device, (ulong)sizeof(CameraData), RhiNative.BufferUsage.Storage);
        _lightBuffer = RhiBuffer.Create(
            device, 1024 * (ulong)sizeof(LightData), RhiNative.BufferUsage.Storage);
    }

    public void Prepare(long frameNumber, float aspect, uint width, uint height)
    {
        if (_preparedFrame == frameNumber)
            return;

        SceneDataExtractor.Extract(
            _device,
            _world,
            _scene,
            _bindlessHeap,
            aspect,
            ref _cameraBuffer,
            ref _lightBuffer,
            ref _instanceBuffer,
            ref _partBuffer,
            ref _materialBuffer,
            _renderer.ActiveCameraEntity,
            Vector3.UnitZ,
            unchecked((uint)frameNumber),
            0,
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
        _instanceBuffer.Dispose();
        _partBuffer.Dispose();
        _materialBuffer.Dispose();
        _cameraBuffer.Dispose();
        _lightBuffer.Dispose();
    }
}
