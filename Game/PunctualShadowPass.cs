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
    private readonly record struct RetiredTileSet(
        long ReleaseFrame,
        ShadowAtlasAllocation[] StaticTiles,
        ShadowAtlasAllocation[] DynamicTiles);

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
        public Vector4 CandidateLightPosition;
        public Vector4 CommittedLightPosition;
        public int UpdateIntervalFrames = 1;
        public float VisualPriority;
        public int ResolutionSubdivision = 32;
        public int ResolutionTargetSubdivision = 32;
        public int ResolutionTargetFrames;
        public long ReadyFrame;
    }

    private const int MaximumLights = 1024;
    private const int FacesPerLight = 6;
    private readonly ShadowAtlas _atlas;
    private readonly RhiBindlessHeap _bindlessHeap;
    private readonly Dictionary<int, uint> _pageSlots = new();
    private readonly Dictionary<ulong, LightEntry> _entries = new();
    private readonly List<RetiredTileSet> _retiredTileSets = new();
    private readonly object _entryLock = new();
    private readonly PunctualShadowFaceData[] _faceData =
        new PunctualShadowFaceData[MaximumLights * FacesPerLight];
    private long _frameNumber;

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
                        entry.UpdateIntervalFrames,
                        entry.LastUpdatedFrames[faceIndex] < 0
                            ? int.MaxValue
                            : (int)Math.Min(
                                _frameNumber -
                                    entry.LastUpdatedFrames[faceIndex],
                                int.MaxValue),
                        entry.VisualPriority,
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
        int preferredSubdivision,
        long frameNumber)
    {
        lock (_entryLock)
            return GetOrAllocateLocked(
                entityId,
                lightIndex,
                faceCount,
                preferredSubdivision,
                frameNumber);
    }

    private LightEntry? GetOrAllocateLocked(
        ulong entityId,
        int lightIndex,
        int faceCount,
        int preferredSubdivision,
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
                preferredSubdivision,
                out ShadowAtlasAllocation[] staticTiles))
        {
            return null;
        }
        if (!_atlas.TryAllocateTileSet(
                faceCount,
                preferredSubdivision,
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
            ResolutionSubdivision = GetSubdivision(
                staticTiles,
                dynamicTiles),
            ResolutionTargetSubdivision = preferredSubdivision,
            ReadyFrame = frameNumber + 1,
        };
        _entries.Add(entityId, entry);
        RegisterPages(staticTiles);
        RegisterPages(dynamicTiles);
        return entry;
    }

    public bool IsResolutionChangeReady(
        LightEntry entry,
        int preferredSubdivision)
    {
        if (entry.ResolutionTargetSubdivision != preferredSubdivision)
        {
            entry.ResolutionTargetSubdivision = preferredSubdivision;
            entry.ResolutionTargetFrames = 1;
            return false;
        }

        if (entry.ResolutionSubdivision == preferredSubdivision)
        {
            entry.ResolutionTargetFrames = 0;
            return false;
        }

        entry.ResolutionTargetFrames++;
        int stableFrameRequirement =
            preferredSubdivision < entry.ResolutionSubdivision
                ? 12
                : 90;
        return entry.ResolutionTargetFrames >= stableFrameRequirement;
    }

    public bool TryApplyResolutionChange(LightEntry entry)
    {
        int preferredSubdivision =
            entry.ResolutionTargetSubdivision;
        if (!_atlas.TryAllocateTileSet(
                entry.FaceCount,
                preferredSubdivision,
                out ShadowAtlasAllocation[] staticTiles))
        {
            entry.ResolutionTargetFrames = 0;
            return false;
        }
        if (!_atlas.TryAllocateTileSet(
                entry.FaceCount,
                preferredSubdivision,
                out ShadowAtlasAllocation[] dynamicTiles))
        {
            foreach (ShadowAtlasAllocation tile in staticTiles)
                _atlas.Release(tile);
            entry.ResolutionTargetFrames = 0;
            return false;
        }

        int subdivision = GetSubdivision(
            staticTiles,
            dynamicTiles);
        if (subdivision == entry.ResolutionSubdivision)
        {
            foreach (ShadowAtlasAllocation tile in staticTiles)
                _atlas.Release(tile);
            foreach (ShadowAtlasAllocation tile in dynamicTiles)
                _atlas.Release(tile);
            entry.ResolutionTargetFrames = 0;
            return false;
        }

        _retiredTileSets.Add(new RetiredTileSet(
            _frameNumber + 3,
            entry.StaticTiles,
            entry.DynamicTiles));
        entry.StaticTiles = staticTiles;
        entry.DynamicTiles = dynamicTiles;
        entry.ResolutionSubdivision = subdivision;
        entry.ResolutionTargetFrames = 0;
        RegisterPages(staticTiles);
        RegisterPages(dynamicTiles);
        Array.Clear(entry.StaticValid);
        Array.Clear(entry.DynamicValid);
        return true;
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
            CommittedLightPosition = entry.CommittedLightPosition,
        };
    }

    public void BeginFrame(long frameNumber)
    {
        _frameNumber = frameNumber;
        for (int index = _retiredTileSets.Count - 1;
             index >= 0;
             --index)
        {
            RetiredTileSet retired = _retiredTileSets[index];
            if (retired.ReleaseFrame > frameNumber)
                continue;
            foreach (ShadowAtlasAllocation tile in retired.StaticTiles)
                _atlas.Release(tile);
            foreach (ShadowAtlasAllocation tile in retired.DynamicTiles)
                _atlas.Release(tile);
            _retiredTileSets.RemoveAt(index);
        }
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

    private static int GetSubdivision(
        ReadOnlySpan<ShadowAtlasAllocation> staticTiles,
        ReadOnlySpan<ShadowAtlasAllocation> dynamicTiles)
    {
        uint smallestTile = ShadowAtlas.PageSize;
        foreach (ShadowAtlasAllocation tile in staticTiles)
            smallestTile = Math.Min(smallestTile, tile.Size);
        foreach (ShadowAtlasAllocation tile in dynamicTiles)
            smallestTile = Math.Min(smallestTile, tile.Size);
        return (int)(ShadowAtlas.PageSize / smallestTile);
    }

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
            foreach (RetiredTileSet retired in _retiredTileSets)
            {
                foreach (ShadowAtlasAllocation tile in retired.StaticTiles)
                    _atlas.Release(tile);
                foreach (ShadowAtlasAllocation tile in retired.DynamicTiles)
                    _atlas.Release(tile);
            }
            _entries.Clear();
            _retiredTileSets.Clear();
            _pageSlots.Clear();
        }
    }
}

internal sealed class PunctualShadowPass : RenderPass, IDisposable
{
    internal const int MaximumFacesPerBatch = 24;

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
    private readonly RhiBuffer[] _pointDrawCommands =
        new RhiBuffer[2];
    private readonly RhiBuffer[] _pointCullJobs =
        new RhiBuffer[2];
    private readonly RhiBuffer[] _spotDrawCommands =
        new RhiBuffer[2];
    private readonly RhiBuffer[] _spotCullJobs =
        new RhiBuffer[2];
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
        for (int batchIndex = 0; batchIndex < 2; ++batchIndex)
        {
            _pointDrawCommands[batchIndex] = RhiBuffer.Create(
                device,
                4096 * DrawCommandSize,
                RhiNative.BufferUsage.Storage |
                    RhiNative.BufferUsage.Indirect);
            _pointCullJobs[batchIndex] = RhiBuffer.Create(
                device,
                32 * (ulong)sizeof(PunctualShadowCullJobData),
                RhiNative.BufferUsage.Storage);
            _spotDrawCommands[batchIndex] = RhiBuffer.Create(
                device,
                4096 * DrawCommandSize,
                RhiNative.BufferUsage.Storage |
                    RhiNative.BufferUsage.Indirect);
            _spotCullJobs[batchIndex] = RhiBuffer.Create(
                device,
                32 * (ulong)sizeof(PunctualShadowCullJobData),
                RhiNative.BufferUsage.Storage);
        }
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
        _state.BeginFrame(context.FrameNumber);
        var candidates = BuildCandidates(frameData);
        var lightWork = new List<LightWork>();
        var tileJobs = new List<TileRenderJob>();
        int renderedUnitCount = 0;
        foreach (Candidate candidate in candidates)
        {
            PunctualShadowState.LightEntry? entry =
                _state.GetOrAllocate(
                    candidate.EntityId,
                    candidate.LightIndex,
                    candidate.FaceCount,
                    candidate.PreferredSubdivision,
                    context.FrameNumber);
            if (entry == null ||
                context.FrameNumber < entry.ReadyFrame)
                continue;

            entry.CandidateLightSignature = candidate.LightSignature;
            entry.CandidateLightPosition = candidate.Light.Position;
            entry.UpdateIntervalFrames =
                candidate.UpdateIntervalFrames;
            entry.VisualPriority = candidate.VisualPriority;
            bool resolutionDirty =
                _state.IsResolutionChangeReady(
                    entry,
                    candidate.PreferredSubdivision);
            BuildFaceMatrices(candidate.Light, entry);

            bool cameraRelevant = false;
            bool lightDirty = false;
            bool staticDirty = false;
            bool dynamicDirty = false;
            long oldestUpdatedFrame = long.MaxValue;
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
                cameraRelevant |= faceCanAffectCamera;
                lightDirty |=
                    !entry.StaticValid[faceIndex] ||
                    !entry.DynamicValid[faceIndex] ||
                    entry.LightSignatures[faceIndex] !=
                        candidate.LightSignature;
                staticDirty |=
                    entry.StaticSceneSignatures[faceIndex] !=
                        staticSignature;
                dynamicDirty |=
                    entry.DynamicSceneSignatures[faceIndex] !=
                        dynamicSignature;
                oldestUpdatedFrame = Math.Min(
                    oldestUpdatedFrame,
                    entry.LastUpdatedFrames[faceIndex]);
                _state.UpdateFace(entry, faceIndex);
            }
            bool invalid = HasInvalidFace(entry);
            long framesSinceUpdate = oldestUpdatedFrame < 0
                ? long.MaxValue
                : context.FrameNumber - oldestUpdatedFrame;
            bool updateDue =
                invalid ||
                resolutionDirty ||
                framesSinceUpdate >= candidate.UpdateIntervalFrames;
            if (cameraRelevant &&
                updateDue &&
                (lightDirty ||
                    staticDirty ||
                    dynamicDirty ||
                    resolutionDirty))
            {
                float urgency = invalid
                    ? float.MaxValue
                    : framesSinceUpdate /
                        (float)candidate.UpdateIntervalFrames +
                        MathF.Min(
                            (float)framesSinceUpdate,
                            60.0f) * 0.02f;
                lightWork.Add(new LightWork(
                    entry,
                    candidate.LightSignature,
                    candidate.Priority,
                    urgency,
                    lightDirty,
                    staticDirty,
                    dynamicDirty,
                    resolutionDirty));
            }
        }

        lightWork.Sort(CompareLightWork);
        int minimumAtomicFaces = 1;
        foreach (LightWork work in lightWork)
        {
            if (work.Entry.FaceCount == 6)
            {
                minimumAtomicFaces = 6;
                break;
            }
        }
        int frameFaceLimit = _scheduler.GetUnitAllowance(
            GpuWorkDomain.PunctualShadows,
            minimumAtomicFaces);
        List<List<LightWork>> batches =
            BuildHomogeneousBatches(
                lightWork,
                frameFaceLimit);
        int batchFaceCount = 0;
        int dirtyFaceCount = 0;
        foreach (LightWork work in lightWork)
            dirtyFaceCount += work.Entry.FaceCount;
        foreach (List<LightWork> batch in batches)
        {
            foreach (LightWork work in batch)
                batchFaceCount += work.Entry.FaceCount;
        }

        bool batchAdmitted =
            batchFaceCount > 0 &&
            _scheduler.TryAdmit(
                GpuWorkDomain.PunctualShadows,
                batchFaceCount);
        _scheduler.Defer(
            GpuWorkDomain.PunctualShadows,
            dirtyFaceCount - batchFaceCount);

        if (batchAdmitted)
        {
            int pointBatchIndex = 0;
            int spotBatchIndex = 0;
            foreach (List<LightWork> batch in batches)
            {
                tileJobs.Clear();
                foreach (LightWork work in batch)
                {
                    PunctualShadowState.LightEntry entry = work.Entry;
                    bool resolutionChanged =
                        work.ResolutionDirty &&
                        _state.TryApplyResolutionChange(entry);
                    bool updateStatic =
                        resolutionChanged ||
                        work.LightDirty ||
                        work.StaticDirty;
                    bool updateDynamic =
                        resolutionChanged ||
                        work.LightDirty ||
                        work.DynamicDirty;
                    for (int faceIndex = 0;
                         faceIndex < entry.FaceCount;
                         ++faceIndex)
                    {
                        Matrix4x4 viewProjection = work.LightDirty
                            ? entry.CandidateViewProjections[faceIndex]
                            : entry.ViewProjections[faceIndex];
                        if (updateStatic)
                        {
                            tileJobs.Add(new TileRenderJob(
                                entry.StaticTiles[faceIndex],
                                viewProjection,
                                1,
                                0));
                        }
                        if (updateDynamic)
                        {
                            tileJobs.Add(new TileRenderJob(
                                entry.DynamicTiles[faceIndex],
                                viewProjection,
                                0,
                                1));
                        }
                    }

                    if (work.LightDirty)
                    {
                        entry.CommittedLightPosition =
                            entry.CandidateLightPosition;
                    }
                    for (int faceIndex = 0;
                         faceIndex < entry.FaceCount;
                         ++faceIndex)
                    {
                        if (work.LightDirty)
                        {
                            entry.ViewProjections[faceIndex] =
                                entry.CandidateViewProjections[faceIndex];
                            entry.LightSignatures[faceIndex] =
                                work.LightSignature;
                        }
                        if (updateStatic)
                        {
                            entry.StaticValid[faceIndex] = true;
                            entry.StaticSceneSignatures[faceIndex] =
                                staticSignature;
                        }
                        if (updateDynamic)
                        {
                            entry.DynamicValid[faceIndex] = true;
                            entry.DynamicSceneSignatures[faceIndex] =
                                dynamicSignature;
                        }
                        entry.LastUpdatedFrames[faceIndex] =
                            context.FrameNumber;
                        _state.UpdateFace(entry, faceIndex);
                    }
                    renderedUnitCount += entry.FaceCount;
                }
                bool pointLights =
                    batch[0].Entry.FaceCount == 6;
                RenderTiles(
                    sink,
                    frameData,
                    tileJobs,
                    pointLights,
                    pointLights
                        ? pointBatchIndex++
                        : spotBatchIndex++);
            }
        }
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
        List<TileRenderJob> jobs,
        bool pointLights,
        int batchIndex)
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
        EnsureBatchBuffers(
            frameData.Parts.Count,
            jobs.Count,
            pointLights,
            batchIndex);
        RhiBuffer drawCommands = pointLights
            ? _pointDrawCommands[batchIndex]
            : _spotDrawCommands[batchIndex];
        RhiBuffer cullJobsBuffer = pointLights
            ? _pointCullJobs[batchIndex]
            : _spotCullJobs[batchIndex];
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
        cullJobsBuffer.Upload(cullJobs);

        var cullPush = new PunctualShadowCullPushData
        {
            Instances = _sceneCache.InstanceBuffer.DeviceAddress,
            Parts = _sceneCache.PartBuffer.DeviceAddress,
            DrawCommands = drawCommands.DeviceAddress,
            Jobs = cullJobsBuffer.DeviceAddress,
            PartCount = (uint)frameData.Parts.Count,
            JobCount = (uint)jobs.Count,
        };
        sink.BeginComputePass("Punctual Shadow Culling");
        sink.BindPipeline(_cullPipeline);
        sink.UseBuffer(_sceneCache.InstanceBuffer, 1);
        sink.UseBuffer(_sceneCache.PartBuffer, 1);
        sink.UseBuffer(drawCommands, 2);
        sink.UseBuffer(cullJobsBuffer, 1);
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
            sink.UseBuffer(drawCommands, 1);
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
                    drawCommands,
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
        float Priority,
        float VisualPriority,
        int UpdateIntervalFrames,
        int PreferredSubdivision);

    private readonly record struct LightWork(
        PunctualShadowState.LightEntry Entry,
        int LightSignature,
        float Priority,
        float Urgency,
        bool LightDirty,
        bool StaticDirty,
        bool DynamicDirty,
        bool ResolutionDirty);

    private readonly record struct TileRenderJob(
        ShadowAtlasAllocation Tile,
        Matrix4x4 ViewProjection,
        uint RequiredFlags,
        uint RejectedFlags);

    private static int CompareLightWork(
        LightWork left,
        LightWork right)
    {
        bool leftInvalid = HasInvalidFace(left.Entry);
        bool rightInvalid = HasInvalidFace(right.Entry);
        int validityOrder = rightInvalid.CompareTo(leftInvalid);
        if (validityOrder != 0)
            return validityOrder;

        int transformOrder =
            right.LightDirty.CompareTo(left.LightDirty);
        if (transformOrder != 0)
            return transformOrder;

        float leftScore =
            GetSchedulingScore(
                left.Priority,
                left.Urgency);
        float rightScore =
            GetSchedulingScore(
                right.Priority,
                right.Urgency);
        return rightScore.CompareTo(leftScore);
    }

    internal static float GetSchedulingScore(
        float priority,
        float urgency)
    {
        return urgency + priority * 2.0f;
    }

    private static bool HasInvalidFace(
        PunctualShadowState.LightEntry entry)
    {
        for (int faceIndex = 0;
             faceIndex < entry.FaceCount;
             ++faceIndex)
        {
            if (!entry.StaticValid[faceIndex] ||
                !entry.DynamicValid[faceIndex])
            {
                return true;
            }
        }
        return false;
    }

    private static List<List<LightWork>> BuildHomogeneousBatches(
        List<LightWork> work,
        int frameFaceLimit)
    {
        var batches = new List<List<LightWork>>(2);
        int admittedFaces = 0;
        foreach (LightWork candidate in work)
        {
            int facesPerLight = candidate.Entry.FaceCount;
            if (admittedFaces + facesPerLight > frameFaceLimit)
            {
                continue;
            }
            List<LightWork>? batch = null;
            foreach (List<LightWork> existingBatch in batches)
            {
                if (existingBatch[0].Entry.FaceCount ==
                        facesPerLight &&
                    GetBatchFaceCount(existingBatch) +
                        facesPerLight <= MaximumFacesPerBatch)
                {
                    batch = existingBatch;
                    break;
                }
            }
            if (batch == null)
            {
                batch = new List<LightWork>();
                batches.Add(batch);
            }
            batch.Add(candidate);
            admittedFaces += facesPerLight;
        }
        return batches;
    }

    private static int GetBatchFaceCount(List<LightWork> batch)
    {
        int faceCount = 0;
        foreach (LightWork work in batch)
            faceCount += work.Entry.FaceCount;
        return faceCount;
    }

    internal static int GetMaximumLightsPerBatch(int facesPerLight)
    {
        if (facesPerLight <= 0)
            throw new ArgumentOutOfRangeException(nameof(facesPerLight));
        return MaximumFacesPerBatch / facesPerLight;
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
            Vector3 influenceCenter = GetInfluenceCenter(light);
            float influenceRadius = GetInfluenceRadius(light);
            float distanceToLight =
                Vector3.Distance(cameraPosition, influenceCenter);
            Vector3 cameraForward = new(
                frameData.Camera.CameraForward.X,
                frameData.Camera.CameraForward.Y,
                frameData.Camera.CameraForward.Z);
            cameraForward = cameraForward.LengthSquared() > 1e-6f
                ? Vector3.Normalize(cameraForward)
                : Vector3.UnitZ;
            float viewDepth = MathF.Max(
                Vector3.Dot(
                    influenceCenter - cameraPosition,
                    cameraForward),
                0.1f);
            float projectionScale = MathF.Max(
                MathF.Abs(frameData.Camera.ViewProj.M11),
                MathF.Abs(frameData.Camera.ViewProj.M22));
            float projectedScreenRadius =
                influenceRadius * projectionScale / viewDepth;
            float visualPriority = GetVisualPriority(
                projectedScreenRadius,
                distanceToLight);
            int updateIntervalFrames = GetUpdateIntervalFrames(
                projectedScreenRadius,
                distanceToLight);
            int preferredSubdivision =
                GetPreferredSubdivision(
                    type == 1 ? 6 : 1,
                    visualPriority);
            float intensityPriority =
                Math.Clamp(
                    MathF.Log2(1.0f + MathF.Max(light.Color.W, 0.0f)) /
                        16.0f,
                    0.0f,
                    0.2f);
            float priority =
                visualPriority + intensityPriority;
            candidates.Add(new Candidate(
                frameData.LightEntityIds[lightIndex],
                lightIndex,
                type == 1 ? 6 : 1,
                light,
                HashCode.Combine(
                    light.Position,
                    light.Direction,
                    light.ShapeParams),
                priority,
                visualPriority,
                updateIntervalFrames,
                preferredSubdivision));
        }
        candidates.Sort(
            (left, right) =>
                right.Priority.CompareTo(left.Priority));
        return candidates;
    }

    internal static int GetUpdateIntervalFrames(
        float projectedScreenRadius,
        float distanceToLight)
    {
        int distanceInterval =
            distanceToLight <= 6.0f ? 1 :
            distanceToLight <= 18.0f ? 2 :
            distanceToLight <= 35.0f ? 3 :
            distanceToLight <= 60.0f ? 5 :
            distanceToLight <= 100.0f ? 8 :
            10;
        int screenInterval =
            projectedScreenRadius >= 0.35f ? 3 :
            projectedScreenRadius >= 0.15f ? 5 :
            projectedScreenRadius >= 0.05f ? 8 :
            10;
        return Math.Min(
            distanceInterval,
            screenInterval);
    }

    private static float GetVisualPriority(
        float projectedScreenRadius,
        float distanceToLight)
    {
        float screenPriority = Math.Clamp(
            projectedScreenRadius / 0.35f,
            0.0f,
            1.0f);
        float distancePriority =
            1.0f /
            (1.0f + MathF.Max(distanceToLight, 0.0f) / 20.0f);
        float combinedPriority =
            screenPriority * 0.65f +
            distancePriority * 0.35f;
        return MathF.Max(
            distancePriority,
            combinedPriority);
    }

    internal static int GetPreferredSubdivision(
        int faceCount,
        float visualPriority)
    {
        if (faceCount == 6)
        {
            if (visualPriority >= 0.8f)
                return 4;
            if (visualPriority >= 0.5f)
                return 8;
            if (visualPriority >= 0.2f)
                return 16;
            return 32;
        }

        if (visualPriority >= 0.8f)
            return 2;
        if (visualPriority >= 0.5f)
            return 4;
        if (visualPriority >= 0.2f)
            return 8;
        return 16;
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
        int jobCount,
        bool pointLights,
        int batchIndex)
    {
        if (pointLights)
        {
            EnsureBatchBuffers(
                ref _pointDrawCommands[batchIndex],
                ref _pointCullJobs[batchIndex],
                partCount,
                jobCount);
        }
        else
        {
            EnsureBatchBuffers(
                ref _spotDrawCommands[batchIndex],
                ref _spotCullJobs[batchIndex],
                partCount,
                jobCount);
        }
    }

    private unsafe void EnsureBatchBuffers(
        ref RhiBuffer drawCommands,
        ref RhiBuffer cullJobs,
        int partCount,
        int jobCount)
    {
        ulong requiredDrawBytes = checked(
            (ulong)partCount *
            (ulong)jobCount *
            DrawCommandSize);
        if (drawCommands.Size < requiredDrawBytes)
        {
            ulong size = drawCommands.Size;
            while (size < requiredDrawBytes)
                size *= 2;
            drawCommands.Dispose();
            drawCommands = RhiBuffer.Create(
                _device,
                size,
                RhiNative.BufferUsage.Storage |
                    RhiNative.BufferUsage.Indirect);
        }

        ulong requiredJobBytes = checked(
            (ulong)jobCount *
            (ulong)sizeof(PunctualShadowCullJobData));
        if (cullJobs.Size >= requiredJobBytes)
            return;
        ulong jobBufferSize = cullJobs.Size;
        while (jobBufferSize < requiredJobBytes)
            jobBufferSize *= 2;
        cullJobs.Dispose();
        cullJobs = RhiBuffer.Create(
            _device,
            jobBufferSize,
            RhiNative.BufferUsage.Storage);
    }

    public void Dispose()
    {
        for (int batchIndex = 0; batchIndex < 2; ++batchIndex)
        {
            _spotCullJobs[batchIndex].Dispose();
            _spotDrawCommands[batchIndex].Dispose();
            _pointCullJobs[batchIndex].Dispose();
            _pointDrawCommands[batchIndex].Dispose();
        }
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
