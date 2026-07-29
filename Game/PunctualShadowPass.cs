// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;

namespace Engine.Game;

internal sealed class PunctualShadowState : IDisposable
{
    internal sealed class LightEntry
    {
        public required ulong EntityId;
        public required int LightIndex;
        public required int FaceCount;
        public required ShadowAtlasAllocation[] StaticTiles;
        public required ShadowAtlasAllocation[] DynamicTiles;
        public Matrix4x4[] ViewProjections { get; } =
            new Matrix4x4[6];
        public Matrix4x4[] CandidateViewProjections { get; } =
            new Matrix4x4[6];
        public bool[] StaticValid { get; } = new bool[6];
        public bool[] DynamicValid { get; } = new bool[6];
        public int[] LightSignatures { get; } = new int[6];
        public int[] StaticSceneSignatures { get; } = new int[6];
        public int[] DynamicSceneSignatures { get; } = new int[6];
        public bool[] CameraRelevant { get; } = new bool[6];
        public long[] LastUpdatedFrames { get; } =
            { -1, -1, -1, -1, -1, -1 };
        public int CandidateLightSignature;
        public long ReadyFrame;
    }

    private const int MaximumLights = 1024;
    private const int FacesPerLight = 6;
    private readonly ShadowAtlas _atlas;
    private readonly RhiBindlessHeap _bindlessHeap;
    private readonly Dictionary<int, uint> _pageSlots = new();
    private readonly Dictionary<ulong, LightEntry> _entries = new();
    private readonly object _entryLock = new();
    private readonly PunctualShadowFaceData[] _faceData =
        new PunctualShadowFaceData[MaximumLights * FacesPerLight];

    public RhiBuffer FaceBuffer { get; }

    public RenderGraphShadowDiagnostics GetDiagnostics()
    {
        lock (_entryLock)
        {
            var faces = new List<RenderGraphShadowFaceDiagnostics>();
            foreach (LightEntry entry in _entries.Values)
            {
                for (int faceIndex = 0;
                     faceIndex < entry.FaceCount;
                     ++faceIndex)
                {
                    ShadowAtlasAllocation staticTile =
                        entry.StaticTiles[faceIndex];
                    ShadowAtlasAllocation dynamicTile =
                        entry.DynamicTiles[faceIndex];
                    faces.Add(new RenderGraphShadowFaceDiagnostics(
                        entry.EntityId,
                        entry.LightIndex,
                        faceIndex,
                        entry.CameraRelevant[faceIndex],
                        entry.LightSignatures[faceIndex] !=
                            entry.CandidateLightSignature,
                        entry.StaticValid[faceIndex],
                        entry.DynamicValid[faceIndex],
                        staticTile.PageIndex,
                        staticTile.SlotIndex,
                        dynamicTile.PageIndex,
                        dynamicTile.SlotIndex,
                        staticTile.X,
                        staticTile.Y,
                        staticTile.Size));
                }
            }
            faces.Sort(
                (left, right) =>
                {
                    int lightOrder =
                        left.LightIndex.CompareTo(right.LightIndex);
                    return lightOrder != 0
                        ? lightOrder
                        : left.FaceIndex.CompareTo(right.FaceIndex);
                });
            return new RenderGraphShadowDiagnostics(
                _atlas.BudgetBytes,
                _atlas.AllocatedBytes,
                _atlas.Pages.Count,
                _entries.Count,
                faces.ToArray());
        }
    }

    public bool TryGetTile(
        ulong entityId,
        int faceIndex,
        bool dynamicTile,
        out ShadowAtlasAllocation allocation)
    {
        lock (_entryLock)
        {
            if (_entries.TryGetValue(
                    entityId,
                    out LightEntry? entry) &&
                (uint)faceIndex < (uint)entry.FaceCount)
            {
                allocation = dynamicTile
                    ? entry.DynamicTiles[faceIndex]
                    : entry.StaticTiles[faceIndex];
                return true;
            }
        }
        allocation = default;
        return false;
    }

    public unsafe PunctualShadowState(
        RhiDevice device,
        ShadowAtlas atlas,
        RhiBindlessHeap bindlessHeap)
    {
        _atlas = atlas;
        _bindlessHeap = bindlessHeap;
        FaceBuffer = RhiBuffer.Create(
            device,
            (ulong)_faceData.Length *
                (ulong)sizeof(PunctualShadowFaceData),
            RhiNative.BufferUsage.Storage);
    }

    public LightEntry? GetOrAllocate(
        ulong entityId,
        int lightIndex,
        int faceCount,
        long frameNumber)
    {
        lock (_entryLock)
            return GetOrAllocateLocked(
                entityId,
                lightIndex,
                faceCount,
                frameNumber);
    }

    private LightEntry? GetOrAllocateLocked(
        ulong entityId,
        int lightIndex,
        int faceCount,
        long frameNumber)
    {
        if (_entries.TryGetValue(entityId, out LightEntry? existing))
        {
            existing.LightIndex = lightIndex;
            return existing;
        }
        if (lightIndex >= MaximumLights ||
            !_atlas.TryAllocateTileSet(
                faceCount,
                out ShadowAtlasAllocation[] staticTiles))
        {
            return null;
        }
        if (!_atlas.TryAllocateTileSet(
                faceCount,
                out ShadowAtlasAllocation[] dynamicTiles))
        {
            foreach (ShadowAtlasAllocation tile in staticTiles)
                _atlas.Release(tile);
            return null;
        }

        var entry = new LightEntry
        {
            EntityId = entityId,
            LightIndex = lightIndex,
            FaceCount = faceCount,
            StaticTiles = staticTiles,
            DynamicTiles = dynamicTiles,
            ReadyFrame = frameNumber + 1,
        };
        _entries.Add(entityId, entry);
        RegisterPages(staticTiles);
        RegisterPages(dynamicTiles);
        return entry;
    }

    public void UpdateFace(
        LightEntry entry,
        int faceIndex)
    {
        ShadowAtlasAllocation staticTile =
            entry.StaticTiles[faceIndex];
        ShadowAtlasAllocation dynamicTile =
            entry.DynamicTiles[faceIndex];
        int dataIndex = entry.LightIndex * FacesPerLight + faceIndex;
        _faceData[dataIndex] = new PunctualShadowFaceData
        {
            ViewProjection = entry.ViewProjections[faceIndex],
            StaticUvScaleBias = GetUvScaleBias(staticTile),
            DynamicUvScaleBias = GetUvScaleBias(dynamicTile),
            TextureIndicesAndFlags = new Vector4(
                GetPageSlot(staticTile.PageIndex),
                GetPageSlot(dynamicTile.PageIndex),
                entry.StaticValid[faceIndex] ? 1.0f : 0.0f,
                entry.DynamicValid[faceIndex] ? 1.0f : 0.0f),
        };
    }

    public void BeginFrame()
    {
        Array.Clear(_faceData);
    }

    public void Upload()
    {
        FaceBuffer.Upload(_faceData);
    }

    private void RegisterPages(
        ReadOnlySpan<ShadowAtlasAllocation> allocations)
    {
        foreach (ShadowAtlasAllocation allocation in allocations)
        {
            if (!_pageSlots.ContainsKey(allocation.PageIndex))
            {
                _pageSlots.Add(
                    allocation.PageIndex,
                    _bindlessHeap.Register(allocation.Texture));
            }
        }
    }

    private uint GetPageSlot(int pageIndex) => _pageSlots[pageIndex];

    private static Vector4 GetUvScaleBias(
        ShadowAtlasAllocation tile)
    {
        float scale = tile.Size / (float)ShadowAtlas.PageSize;
        return new Vector4(
            scale,
            scale,
            tile.X / (float)ShadowAtlas.PageSize,
            tile.Y / (float)ShadowAtlas.PageSize);
    }

    public void Dispose()
    {
        FaceBuffer.Dispose();
        lock (_entryLock)
        {
            foreach (uint slot in _pageSlots.Values)
                _bindlessHeap.Release(slot);
            foreach (LightEntry entry in _entries.Values)
            {
                foreach (ShadowAtlasAllocation tile in entry.StaticTiles)
                    _atlas.Release(tile);
                foreach (ShadowAtlasAllocation tile in entry.DynamicTiles)
                    _atlas.Release(tile);
            }
            _entries.Clear();
            _pageSlots.Clear();
        }
    }
}

internal sealed class PunctualShadowPass : RenderPass, IDisposable
{
    private const ulong DrawCommandSize = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct PunctualShadowCullJobData
    {
        public Matrix4x4 ViewProjection;
        public uint RequiredInstanceFlags;
        public uint RejectedInstanceFlags;
        public uint Pad0;
        public uint Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PunctualShadowCullPushData
    {
        public ulong Instances;
        public ulong Parts;
        public ulong DrawCommands;
        public ulong Jobs;
        public uint PartCount;
        public uint JobCount;
        public uint Pad0;
        public uint Pad1;
    }

    private readonly RasterSceneGpuCache _sceneCache;
    private readonly RhiDevice _device;
    private readonly PunctualShadowState _state;
    private readonly GpuWorkScheduler _scheduler;
    private readonly RhiShader _depthVertexShader;
    private readonly RhiShader _depthFragmentShader;
    private readonly RhiShader _cullShader;
    private readonly RhiShader _clearVertexShader;
    private readonly RhiShader _clearFragmentShader;
    private readonly RhiPipeline _depthPipeline;
    private readonly RhiPipeline _cullPipeline;
    private readonly RhiPipeline _clearPipeline;
    private RhiBuffer _drawCommands;
    private RhiBuffer _cullJobs;
    private readonly long[] _renderedFrames = new long[16];
    private readonly int[] _renderedUnitCounts = new int[16];

    public unsafe PunctualShadowPass(
        RhiDevice device,
        string contentRoot,
        RasterSceneGpuCache sceneCache,
        PunctualShadowState state,
        GpuWorkScheduler scheduler)
    {
        Name = "Punctual Shadows";
        _device = device;
        _sceneCache = sceneCache;
        _state = state;
        _scheduler = scheduler;
        string shaderDirectory = Path.Combine(contentRoot, "shaders");
        string depthSource = File.ReadAllText(
            Path.Combine(shaderDirectory, "shadow_depth.slang"));
        _depthVertexShader = RhiShader.FromSource(
            device,
            depthSource,
            "vertexMain",
            RhiNative.ShaderStage.Vertex,
            shaderDirectory);
        _depthFragmentShader = RhiShader.FromSource(
            device,
            depthSource,
            "fragmentMain",
            RhiNative.ShaderStage.Fragment,
            shaderDirectory);
        _depthPipeline = RhiPipeline.CreateDepthOnly(
            device,
            _depthVertexShader,
            _depthFragmentShader);
        _cullShader = RhiShader.FromSource(
            device,
            File.ReadAllText(
                Path.Combine(
                    shaderDirectory,
                    "punctual_shadow_cull.slang")),
            "computeMain",
            RhiNative.ShaderStage.Compute,
            shaderDirectory);
        _cullPipeline = RhiPipeline.CreateCompute(device, _cullShader);
        string clearSource = File.ReadAllText(
            Path.Combine(shaderDirectory, "shadow_tile_clear.slang"));
        _clearVertexShader = RhiShader.FromSource(
            device,
            clearSource,
            "vertexMain",
            RhiNative.ShaderStage.Vertex,
            shaderDirectory);
        _clearFragmentShader = RhiShader.FromSource(
            device,
            clearSource,
            "fragmentMain",
            RhiNative.ShaderStage.Fragment,
            shaderDirectory);
        _clearPipeline = RhiPipeline.CreateDepthClear(
            device,
            _clearVertexShader,
            _clearFragmentShader);
        _drawCommands = RhiBuffer.Create(
            device,
            4096 * DrawCommandSize,
            RhiNative.BufferUsage.Storage |
                RhiNative.BufferUsage.Indirect);
        _cullJobs = RhiBuffer.Create(
            device,
            32 * (ulong)sizeof(PunctualShadowCullJobData),
            RhiNative.BufferUsage.Storage);
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        for (int pageIndex = 4; pageIndex < 24; ++pageIndex)
        {
            builder.Write(
                Renderer.GetShadowPageHandle(pageIndex),
                ResourceState.DepthStencil);
        }
    }

    public override unsafe void Execute(
        ICommandSink sink,
        RenderGraphContext context)
    {
        uint width = Math.Max(context.Width, 1);
        uint height = Math.Max(context.Height, 1);
        _sceneCache.Prepare(
            context.FrameNumber,
            (float)width / height,
            width,
            height);
        SceneFrameData frameData = _sceneCache.FrameData;
        if (frameData.Parts.Count == 0)
            return;
        _scheduler.BeginFrame(context.FrameNumber);

        int staticSignature = ComputeSceneSignature(
            frameData,
            staticCasters: true);
        int dynamicSignature = ComputeSceneSignature(
            frameData,
            staticCasters: false);
        _state.BeginFrame();
        var candidates = BuildCandidates(frameData);
        var faceWork = new List<FaceWork>();
        var tileJobs = new List<TileRenderJob>();
        int renderedUnitCount = 0;
        foreach (Candidate candidate in candidates)
        {
            PunctualShadowState.LightEntry? entry =
                _state.GetOrAllocate(
                    candidate.EntityId,
                    candidate.LightIndex,
                    candidate.FaceCount,
                    context.FrameNumber);
            if (entry == null ||
                context.FrameNumber < entry.ReadyFrame)
                continue;

            entry.CandidateLightSignature = candidate.LightSignature;
            BuildFaceMatrices(candidate.Light, entry);

            for (int faceIndex = 0;
                 faceIndex < entry.FaceCount;
                 ++faceIndex)
            {
                Matrix4x4 candidateViewProjection =
                    entry.CandidateViewProjections[faceIndex];
                bool faceCanAffectCamera =
                    entry.FaceCount == 6 ||
                    FaceCanAffectCamera(
                        candidateViewProjection,
                        frameData.Camera);
                entry.CameraRelevant[faceIndex] = faceCanAffectCamera;
                bool lightDirty =
                    !entry.StaticValid[faceIndex] ||
                    !entry.DynamicValid[faceIndex] ||
                    entry.LightSignatures[faceIndex] !=
                        candidate.LightSignature;
                bool staticDirty =
                    entry.StaticSceneSignatures[faceIndex] !=
                        staticSignature;
                bool dynamicDirty =
                    entry.DynamicSceneSignatures[faceIndex] !=
                        dynamicSignature;
                _state.UpdateFace(entry, faceIndex);
                if (faceCanAffectCamera &&
                    (lightDirty || staticDirty || dynamicDirty))
                {
                    faceWork.Add(new FaceWork(
                        entry,
                        faceIndex,
                        candidate.LightSignature,
                        candidate.Priority,
                        lightDirty,
                        staticDirty,
                        dynamicDirty));
                }
            }
        }

        faceWork.Sort(CompareFaceWork);
        foreach (FaceWork work in faceWork)
        {
            PunctualShadowState.LightEntry entry = work.Entry;
            int faceIndex = work.FaceIndex;
            if (work.LightDirty &&
                _scheduler.TryAdmit(
                    GpuWorkDomain.PunctualShadows,
                    2))
            {
                Matrix4x4 candidateViewProjection =
                    entry.CandidateViewProjections[faceIndex];
                tileJobs.Add(new TileRenderJob(
                    entry.StaticTiles[faceIndex],
                    candidateViewProjection,
                    1,
                    0));
                tileJobs.Add(new TileRenderJob(
                    entry.DynamicTiles[faceIndex],
                    candidateViewProjection,
                    0,
                    1));
                entry.ViewProjections[faceIndex] =
                    candidateViewProjection;
                entry.StaticValid[faceIndex] = true;
                entry.DynamicValid[faceIndex] = true;
                entry.LightSignatures[faceIndex] =
                    work.LightSignature;
                entry.StaticSceneSignatures[faceIndex] =
                    staticSignature;
                entry.DynamicSceneSignatures[faceIndex] =
                    dynamicSignature;
                entry.LastUpdatedFrames[faceIndex] =
                    context.FrameNumber;
                renderedUnitCount += 2;
            }
            else if (!work.LightDirty)
            {
                bool updated = false;
                if (work.StaticDirty &&
                    _scheduler.TryAdmit(
                        GpuWorkDomain.PunctualShadows))
                {
                    tileJobs.Add(new TileRenderJob(
                        entry.StaticTiles[faceIndex],
                        entry.ViewProjections[faceIndex],
                        1,
                        0));
                    entry.StaticSceneSignatures[faceIndex] =
                        staticSignature;
                    renderedUnitCount++;
                    updated = true;
                }
                if (work.DynamicDirty &&
                    _scheduler.TryAdmit(
                        GpuWorkDomain.PunctualShadows))
                {
                    tileJobs.Add(new TileRenderJob(
                        entry.DynamicTiles[faceIndex],
                        entry.ViewProjections[faceIndex],
                        0,
                        1));
                    entry.DynamicSceneSignatures[faceIndex] =
                        dynamicSignature;
                    renderedUnitCount++;
                    updated = true;
                }
                if (updated)
                {
                    entry.LastUpdatedFrames[faceIndex] =
                        context.FrameNumber;
                }
            }
            _state.UpdateFace(entry, faceIndex);
        }
        RenderTiles(sink, frameData, tileJobs);
        _state.Upload();
        int historyIndex = (int)(context.FrameNumber & 15);
        _renderedFrames[historyIndex] = context.FrameNumber;
        _renderedUnitCounts[historyIndex] = renderedUnitCount;
    }

    public bool TryGetRenderedUnitCount(
        long frameNumber,
        out int count)
    {
        int historyIndex = (int)(frameNumber & 15);
        count = _renderedUnitCounts[historyIndex];
        return _renderedFrames[historyIndex] == frameNumber;
    }

    private unsafe void RenderTiles(
        ICommandSink sink,
        SceneFrameData frameData,
        List<TileRenderJob> jobs)
    {
        if (jobs.Count == 0)
            return;

        jobs.Sort(static (left, right) =>
        {
            int pageOrder =
                left.Tile.PageIndex.CompareTo(right.Tile.PageIndex);
            return pageOrder != 0
                ? pageOrder
                : left.Tile.SlotIndex.CompareTo(right.Tile.SlotIndex);
        });
        EnsureBatchBuffers(frameData.Parts.Count, jobs.Count);
        var cullJobs = new PunctualShadowCullJobData[jobs.Count];
        for (int jobIndex = 0; jobIndex < jobs.Count; ++jobIndex)
        {
            TileRenderJob job = jobs[jobIndex];
            cullJobs[jobIndex] = new PunctualShadowCullJobData
            {
                ViewProjection = job.ViewProjection,
                RequiredInstanceFlags = job.RequiredFlags,
                RejectedInstanceFlags = job.RejectedFlags,
            };
        }
        _cullJobs.Upload(cullJobs);

        var cullPush = new PunctualShadowCullPushData
        {
            Instances = _sceneCache.InstanceBuffer.DeviceAddress,
            Parts = _sceneCache.PartBuffer.DeviceAddress,
            DrawCommands = _drawCommands.DeviceAddress,
            Jobs = _cullJobs.DeviceAddress,
            PartCount = (uint)frameData.Parts.Count,
            JobCount = (uint)jobs.Count,
        };
        sink.BeginComputePass("Punctual Shadow Culling");
        sink.BindPipeline(_cullPipeline);
        sink.UseBuffer(_sceneCache.InstanceBuffer, 1);
        sink.UseBuffer(_sceneCache.PartBuffer, 1);
        sink.UseBuffer(_drawCommands, 2);
        sink.UseBuffer(_cullJobs, 1);
        sink.PushConstants(
            0,
            (uint)sizeof(PunctualShadowCullPushData),
            (IntPtr)(&cullPush));
        sink.Dispatch(
            ((uint)frameData.Parts.Count + 63) / 64,
            (uint)jobs.Count,
            1);
        sink.EndComputePass();

        int firstJob = 0;
        while (firstJob < jobs.Count)
        {
            int pageIndex = jobs[firstJob].Tile.PageIndex;
            int lastJob = firstJob + 1;
            while (lastJob < jobs.Count &&
                   jobs[lastJob].Tile.PageIndex == pageIndex)
            {
                ++lastJob;
            }

            sink.BeginDepthOnlyPass(
                jobs[firstJob].Tile.Texture,
                RhiNative.LoadOp.Load);
            sink.BindPipeline(_clearPipeline);
            for (int jobIndex = firstJob;
                 jobIndex < lastJob;
                 ++jobIndex)
            {
                ShadowAtlasAllocation tile = jobs[jobIndex].Tile;
                sink.SetViewport(
                    tile.X,
                    tile.Y,
                    tile.Size,
                    tile.Size);
                sink.SetScissor(
                    tile.X,
                    tile.Y,
                    tile.Size,
                    tile.Size);
                sink.Draw(3);
            }

            sink.BindPipeline(_depthPipeline);
            sink.UseBuffer(_sceneCache.InstanceBuffer, 1);
            sink.UseBuffer(_sceneCache.PartBuffer, 1);
            sink.UseBuffer(_drawCommands, 1);
            foreach (var mesh in frameData.UniqueMeshes)
            {
                sink.UseBuffer(mesh.VertexBuffer, 1);
                sink.UseBuffer(mesh.IndexBuffer, 1);
            }
            for (int jobIndex = firstJob;
                 jobIndex < lastJob;
                 ++jobIndex)
            {
                TileRenderJob job = jobs[jobIndex];
                ShadowAtlasAllocation tile = job.Tile;
                sink.SetViewport(
                    tile.X,
                    tile.Y,
                    tile.Size,
                    tile.Size);
                sink.SetScissor(
                    tile.X,
                    tile.Y,
                    tile.Size,
                    tile.Size);
                ScenePushData push = _sceneCache.PushData;
                push.DirectionalShadowViewProj =
                    job.ViewProjection;
                sink.PushConstants(
                    0,
                    (uint)sizeof(ScenePushData),
                    (IntPtr)(&push));
                sink.DrawIndirect(
                    _drawCommands,
                    GetDrawCommandOffset(
                        jobIndex,
                        frameData.Parts.Count),
                    (uint)frameData.Parts.Count,
                    (uint)DrawCommandSize);
            }
            sink.EndPass();
            firstJob = lastJob;
        }
    }

    internal static ulong GetDrawCommandOffset(
        int jobIndex,
        int partCount)
        => checked(
            (ulong)jobIndex *
            (ulong)partCount *
            DrawCommandSize);

    private readonly record struct Candidate(
        ulong EntityId,
        int LightIndex,
        int FaceCount,
        LightData Light,
        int LightSignature,
        float Priority);

    private readonly record struct FaceWork(
        PunctualShadowState.LightEntry Entry,
        int FaceIndex,
        int LightSignature,
        float Priority,
        bool LightDirty,
        bool StaticDirty,
        bool DynamicDirty);

    private readonly record struct TileRenderJob(
        ShadowAtlasAllocation Tile,
        Matrix4x4 ViewProjection,
        uint RequiredFlags,
        uint RejectedFlags);

    private static int CompareFaceWork(
        FaceWork left,
        FaceWork right)
    {
        bool leftInvalid =
            !left.Entry.StaticValid[left.FaceIndex] ||
            !left.Entry.DynamicValid[left.FaceIndex];
        bool rightInvalid =
            !right.Entry.StaticValid[right.FaceIndex] ||
            !right.Entry.DynamicValid[right.FaceIndex];
        int validityOrder = rightInvalid.CompareTo(leftInvalid);
        if (validityOrder != 0)
            return validityOrder;

        int ageOrder =
            left.Entry.LastUpdatedFrames[left.FaceIndex].CompareTo(
                right.Entry.LastUpdatedFrames[right.FaceIndex]);
        if (ageOrder != 0)
            return ageOrder;

        return right.Priority.CompareTo(left.Priority);
    }

    private static List<Candidate> BuildCandidates(
        SceneFrameData frameData)
    {
        var candidates = new List<Candidate>();
        Vector3 cameraPosition = new(
            frameData.Camera.CameraPosition.X,
            frameData.Camera.CameraPosition.Y,
            frameData.Camera.CameraPosition.Z);
        for (int lightIndex = 0;
             lightIndex < frameData.Lights.Count;
             ++lightIndex)
        {
            LightData light = frameData.Lights[lightIndex];
            int type = (int)light.Direction.W;
            if ((type != 1 && type != 2) ||
                light.ShapeParams.W < 0.5f ||
                !SphereIntersectsFrustum(
                    GetInfluenceCenter(light),
                    GetInfluenceRadius(light),
                    frameData.Camera.ViewProj))
            {
                continue;
            }
            Vector3 position = new(
                light.Position.X,
                light.Position.Y,
                light.Position.Z);
            float distance = Vector3.Distance(cameraPosition, position);
            float priority =
                light.Color.W *
                light.Position.W /
                MathF.Max(distance, 0.25f);
            candidates.Add(new Candidate(
                frameData.LightEntityIds[lightIndex],
                lightIndex,
                type == 1 ? 6 : 1,
                light,
                HashCode.Combine(
                    light.Position,
                    light.Direction,
                    light.ShapeParams),
                priority));
        }
        candidates.Sort(
            (left, right) =>
                right.Priority.CompareTo(left.Priority));
        return candidates;
    }

    private static void BuildFaceMatrices(
        LightData light,
        PunctualShadowState.LightEntry entry)
    {
        Vector3 position = new(
            light.Position.X,
            light.Position.Y,
            light.Position.Z);
        float range = MathF.Max(light.Position.W, 0.2f);
        if (entry.FaceCount == 1)
        {
            Vector3 direction = Vector3.Normalize(new Vector3(
                light.Direction.X,
                light.Direction.Y,
                light.Direction.Z));
            Vector3 up = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) >
                0.95f
                ? Vector3.UnitZ
                : Vector3.UnitY;
            float fov = 2.0f * MathF.Acos(
                Math.Clamp(light.ShapeParams.Y, 0.01f, 0.999f));
            entry.CandidateViewProjections[0] =
                Matrix4x4.CreateLookAt(
                    position,
                    position + direction,
                    up) *
                Matrix4x4.CreatePerspectiveFieldOfView(
                    Math.Clamp(fov, 0.05f, MathF.PI - 0.05f),
                    1.0f,
                    0.05f,
                    range);
            return;
        }

        ReadOnlySpan<Vector3> directions =
        [
            Vector3.UnitX,
            -Vector3.UnitX,
            Vector3.UnitY,
            -Vector3.UnitY,
            Vector3.UnitZ,
            -Vector3.UnitZ,
        ];
        ReadOnlySpan<Vector3> upVectors =
        [
            Vector3.UnitY,
            Vector3.UnitY,
            -Vector3.UnitZ,
            Vector3.UnitZ,
            Vector3.UnitY,
            Vector3.UnitY,
        ];
        Matrix4x4 projection =
            Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI * 0.5f,
                1.0f,
                0.05f,
                range);
        for (int faceIndex = 0; faceIndex < 6; ++faceIndex)
        {
            entry.CandidateViewProjections[faceIndex] =
                Matrix4x4.CreateLookAt(
                    position,
                    position + directions[faceIndex],
                    upVectors[faceIndex]) *
                projection;
        }
    }

    private static bool FaceCanAffectCamera(
        Matrix4x4 lightViewProjection,
        CameraData camera)
    {
        if (FrustumCornersOverlap(
                camera.InvViewProj,
                lightViewProjection))
        {
            return true;
        }
        if (!Matrix4x4.Invert(
                lightViewProjection,
                out Matrix4x4 inverseLightViewProjection))
        {
            return true;
        }
        return FrustumCornersOverlap(
            inverseLightViewProjection,
            camera.ViewProj);
    }

    private static bool FrustumCornersOverlap(
        Matrix4x4 inverseSourceViewProjection,
        Matrix4x4 targetViewProjection)
    {
        for (int z = 0; z <= 1; ++z)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int x = -1; x <= 1; x += 2)
                {
                    Vector4 world = Vector4.Transform(
                        new Vector4(x, y, z, 1.0f),
                        inverseSourceViewProjection);
                    if (MathF.Abs(world.W) <= 1e-6f)
                        continue;
                    world /= world.W;
                    Vector4 clip = Vector4.Transform(
                        world,
                        targetViewProjection);
                    if (clip.W > 0.0f &&
                        clip.X >= -clip.W &&
                        clip.X <= clip.W &&
                        clip.Y >= -clip.W &&
                        clip.Y <= clip.W &&
                        clip.Z >= 0.0f &&
                        clip.Z <= clip.W)
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    private static Vector3 GetInfluenceCenter(LightData light)
    {
        Vector3 position = new(
            light.Position.X,
            light.Position.Y,
            light.Position.Z);
        if ((int)light.Direction.W != 2)
            return position;
        Vector3 direction = Vector3.Normalize(new Vector3(
            light.Direction.X,
            light.Direction.Y,
            light.Direction.Z));
        return position + direction * (light.Position.W * 0.5f);
    }

    private static float GetInfluenceRadius(LightData light)
        => (int)light.Direction.W == 2
            ? light.Position.W * 0.5f
            : light.Position.W;

    private static bool SphereIntersectsFrustum(
        Vector3 center,
        float radius,
        Matrix4x4 viewProjection)
    {
        Span<Vector4> planes = stackalloc Vector4[6]
        {
            new(
                viewProjection.M14 + viewProjection.M11,
                viewProjection.M24 + viewProjection.M21,
                viewProjection.M34 + viewProjection.M31,
                viewProjection.M44 + viewProjection.M41),
            new(
                viewProjection.M14 - viewProjection.M11,
                viewProjection.M24 - viewProjection.M21,
                viewProjection.M34 - viewProjection.M31,
                viewProjection.M44 - viewProjection.M41),
            new(
                viewProjection.M14 + viewProjection.M12,
                viewProjection.M24 + viewProjection.M22,
                viewProjection.M34 + viewProjection.M32,
                viewProjection.M44 + viewProjection.M42),
            new(
                viewProjection.M14 - viewProjection.M12,
                viewProjection.M24 - viewProjection.M22,
                viewProjection.M34 - viewProjection.M32,
                viewProjection.M44 - viewProjection.M42),
            new(
                viewProjection.M13,
                viewProjection.M23,
                viewProjection.M33,
                viewProjection.M43),
            new(
                viewProjection.M14 - viewProjection.M13,
                viewProjection.M24 - viewProjection.M23,
                viewProjection.M34 - viewProjection.M33,
                viewProjection.M44 - viewProjection.M43),
        };
        foreach (Vector4 plane in planes)
        {
            Vector3 normal = new(
                plane.X,
                plane.Y,
                plane.Z);
            float normalLength = normal.Length();
            if (normalLength <= 1e-6f)
                continue;
            float distance =
                (Vector3.Dot(normal, center) + plane.W) /
                normalLength;
            if (distance < -radius)
                return false;
        }
        return true;
    }

    private static int ComputeSceneSignature(
        SceneFrameData frameData,
        bool staticCasters)
    {
        var hash = new HashCode();
        foreach (InstanceData instance in frameData.Instances)
        {
            bool isStatic = (instance.Flags & 1u) != 0;
            if (isStatic != staticCasters)
                continue;
            hash.Add(instance.ModelMatrix);
            hash.Add(instance.EntityIdLow);
            hash.Add(instance.EntityIdHigh);
        }
        return hash.ToHashCode();
    }

    private unsafe void EnsureBatchBuffers(
        int partCount,
        int jobCount)
    {
        ulong requiredDrawBytes = checked(
            (ulong)partCount *
            (ulong)jobCount *
            DrawCommandSize);
        if (_drawCommands.Size < requiredDrawBytes)
        {
            ulong size = _drawCommands.Size;
            while (size < requiredDrawBytes)
                size *= 2;
            _drawCommands.Dispose();
            _drawCommands = RhiBuffer.Create(
                _device,
                size,
                RhiNative.BufferUsage.Storage |
                    RhiNative.BufferUsage.Indirect);
        }

        ulong requiredJobBytes = checked(
            (ulong)jobCount *
            (ulong)sizeof(PunctualShadowCullJobData));
        if (_cullJobs.Size >= requiredJobBytes)
            return;
        ulong jobBufferSize = _cullJobs.Size;
        while (jobBufferSize < requiredJobBytes)
            jobBufferSize *= 2;
        _cullJobs.Dispose();
        _cullJobs = RhiBuffer.Create(
            _device,
            jobBufferSize,
            RhiNative.BufferUsage.Storage);
    }

    public void Dispose()
    {
        _cullJobs.Dispose();
        _drawCommands.Dispose();
        _clearPipeline.Dispose();
        _cullPipeline.Dispose();
        _depthPipeline.Dispose();
        _clearFragmentShader.Dispose();
        _clearVertexShader.Dispose();
        _cullShader.Dispose();
        _depthFragmentShader.Dispose();
        _depthVertexShader.Dispose();
        _state.Dispose();
    }
}
