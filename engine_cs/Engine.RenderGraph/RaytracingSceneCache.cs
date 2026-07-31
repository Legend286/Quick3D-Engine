// SPDX-License-Identifier: MIT
// Shared scene-mesh raytracing cache. Mirrors what
// Plugins/Renderer.PathTracing/PathTracerPass.UpdateTlas already does
// for the canonical Path Tracing renderer, but exposes the BLAS
// cache + TLAS rebuild as a reusable component so DDGI, future RT
// shadows, future RT reflections, and any other IRaytracingConsumer
// plugin can share one engine-side accelerator build.
//
// Clustered raster pipeline does NOT build or own this cache — it's
// constructed lazily per-frame on demand by the FIRST RT consumer
// plugin to call TryUpdateTlas. This guarantees the raster path
// pays zero TLAS/BLAS cost when no plugin opts into raytracing.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.Assets;
using Engine.CBindings;
using Engine.RHI;
using Engine.Scene;
using Engine.Scene.Components;
// Engine.Scene transitively brings Engine.Assets into scope (its
// own sources `using Engine.Assets;`), which collides with our
// direct `using Engine.Assets;` here for the `Vertex` struct. The
// alias forces the BLAS descriptor's VertexStride to be measured
// against the canonical mesh-vertex struct in Engine.Assets.
using Vertex = Engine.Assets.Vertex;

namespace Engine.RenderGraph;

/// <summary>
/// Process-local cache of BLAS per mesh + a single scene TLAS rebuilt
/// only when entity topology (model set, transform matrix, instance
/// offset) changes. Owners thread this through their compute pass
/// ctor so the pass builds the TLAS lazily on Execute(). The cache is
/// not constructed eagerly so the raster pipeline (Pbr / Grid / Sky /
/// ImGui) never pays for an unused TLAS.
/// </summary>
public sealed class RaytracingSceneCache
{
    private const int TrackedTlasHistory = 3;

    private readonly RhiDevice _device;
    private readonly IEntityStore _world;
    private RhiAccelStruct? _tlas;
    private int _lastInstanceHash;
    private readonly Queue<RhiAccelStruct> _oldTlasQueue = new();

    public RaytracingSceneCache(RhiDevice device, IEntityStore world)
    {
        _device = device;
        _world = world;
    }

    /// <summary>
    /// Hash the scene's entity topology; if it changed since the
    /// last call, dispose the prior TLAS (kept in a 3-deep history
    /// queue so a slow in-flight GPU dispatch can still touch it)
    /// and build a new one. <see cref="TlasUpdateResult.HasGeometry"/>
    /// is false when no entities carry Mesh+Transform so callers
    /// can early-out without binding. <see cref="TlasUpdateResult.TopologyChanged"/>
    /// flips true ONLY when the instance hash differs from the
    /// previous frame plus a TLAS rebuild was issued; path-tracer
    /// uses it to reset its frame accumulator on topology drift.
    /// </summary>
    public unsafe TlasUpdateResult TryUpdateTlas(ICommandSink sink)
    {
        var instances = new List<RhiNative.TlasInstanceDesc>();
        var blasesToBuild = new List<RhiAccelStruct>();

        uint instanceId = 0;
        int hash = 0;

        var validEntities = new List<ulong>(_world.Entities);
        validEntities.Sort();

        foreach (var entityId in validEntities)
        {
            if (!_world.TryGet<ModelComponent>(entityId, out var modelComp))
                continue;

            var transform = _world.TryGet<Transform>(entityId, out var t)
                ? t
                : Transform.Default;

            var model = AssetRegistry.GetModel(modelComp.ModelId);
            if (model == null || model.Parts == null) continue;

            foreach (var part in model.Parts)
            {
                var mesh = AssetRegistry.GetMesh(part.MeshId);
                if (mesh == null) continue;

                if (mesh.Blas == null)
                {
                    var geom = new RhiNative.BlasGeometryDesc
                    {
                        VertexBuffer = mesh.VertexBuffer.Handle,
                        VertexBufferOffset = 0,
                        VertexStride = (uint)sizeof(Vertex),
                        VertexCount = mesh.VertexCount,
                        VertexFormat = RhiNative.VertexFormat.Float3,
                        IndexBuffer = mesh.IndexBuffer.Handle,
                        IndexBufferOffset = 0,
                        IndexCount = mesh.IndexCount,
                        Is32BitIndex = mesh.IndexFormat == 32 ? 1 : 0
                    };

                    var desc = new RhiNative.AccelStructDesc
                    {
                        Abi = 6,
                        Type = RhiNative.AccelStructType.Blas,
                        Geometries = (IntPtr)(&geom),
                        GeometryCount = 1,
                        Instances = IntPtr.Zero,
                        InstanceCount = 0
                    };

                    mesh.Blas = RhiAccelStruct.Create(_device, in desc);
                    blasesToBuild.Add(mesh.Blas);
                }

                var modelMat =
                    Matrix4x4.CreateTranslation(part.LocalOffset) *
                    Matrix4x4.CreateScale(transform.Scale) *
                    Matrix4x4.CreateFromQuaternion(transform.Rotation) *
                    Matrix4x4.CreateTranslation(transform.Position);

                var inst = new RhiNative.TlasInstanceDesc
                {
                    InstanceId = instanceId,
                    Mask = 0xFF,
                    InstanceOffset = 0,
                    Flags = 5u,
                    Blas = mesh.Blas.Handle
                };

                inst.Transform[0]  = modelMat.M11; inst.Transform[1]  = modelMat.M21; inst.Transform[2]  = modelMat.M31; inst.Transform[3]  = modelMat.M41;
                inst.Transform[4]  = modelMat.M12; inst.Transform[5]  = modelMat.M22; inst.Transform[6]  = modelMat.M32; inst.Transform[7]  = modelMat.M42;
                inst.Transform[8]  = modelMat.M13; inst.Transform[9]  = modelMat.M23; inst.Transform[10] = modelMat.M33; inst.Transform[11] = modelMat.M43;

                instances.Add(inst);
                instanceId++;
            }
        }

        if (blasesToBuild.Count > 0)
        {
            var span = CollectionsMarshal.AsSpan(blasesToBuild);
            sink.BuildAccelStructs(span);
        }

        foreach (var inst in instances)
        {
            hash = HashCode.Combine(hash, inst.InstanceId, inst.Blas.GetHashCode());
            for (int i = 0; i < 12; i++)
                hash = HashCode.Combine(hash, inst.Transform[i].GetHashCode());
        }

        bool hasGeometry = instances.Count > 0;
        bool topologyChanged = false;

        if (hash == _lastInstanceHash && _tlas != null)
        {
            if (_oldTlasQueue.Count > TrackedTlasHistory)
                _oldTlasQueue.Dequeue().Dispose();
            return new TlasUpdateResult(_tlas, hasGeometry, false);
        }

        _lastInstanceHash = hash;
        topologyChanged = true;

        if (_tlas != null)
        {
            _oldTlasQueue.Enqueue(_tlas);
            if (_oldTlasQueue.Count > TrackedTlasHistory)
                _oldTlasQueue.Dequeue().Dispose();
            _tlas = null;
        }

        if (hasGeometry)
        {
            var instArr = instances.ToArray();
            _tlas = RhiAccelStruct.CreateTlas(
                _device,
                new ReadOnlySpan<RhiNative.TlasInstanceDesc>(instArr));
            var tlasArr = new RhiAccelStruct[] { _tlas };
            sink.BuildAccelStructs(new ReadOnlySpan<RhiAccelStruct>(tlasArr));
        }

        return new TlasUpdateResult(_tlas, hasGeometry, topologyChanged);
    }

    public readonly record struct TlasUpdateResult(
        RhiAccelStruct? SceneTlas,
        bool HasGeometry,
        bool TopologyChanged);

    public void Dispose()
    {
        _tlas?.Dispose();
        _tlas = null;
        while (_oldTlasQueue.Count > 0)
            _oldTlasQueue.Dequeue().Dispose();
    }
}
