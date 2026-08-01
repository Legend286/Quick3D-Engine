// SPDX-License-Identifier: MIT
using System;
using System.Numerics;
using System.Threading;
using System.Runtime.InteropServices;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;

namespace Engine.DDGI;

/// <summary>
/// Owns the fixed-memory atlas and adaptive clipmap buffers for DDGI.
/// </summary>
/// <remarks>
/// Memory layout:
///   * <see cref="Irradiance"/> is a 4096-wide RGBA16F atlas. Each probe
///     stores four SH coefficients beginning at <c>probeIndex * 4</c>.
///   * <see cref="Visibility"/> is a 4096-wide RGBA16F atlas carrying a
///     4x4 octahedral distance-moment tile per probe.
/// </remarks>
public sealed class DDGIAtlasResources : IDisposable
{
    public const int AtlasWidth = 4096;
    public const int VisibilityTileResolution = 4;
    public const int VisibilityTexelsPerProbe =
        VisibilityTileResolution * VisibilityTileResolution;
    private static int _nextGraphResourceId = 0x60000000;

    public RhiTexture Irradiance { get; }
    public RhiTexture Visibility { get; }
    public RhiBuffer ProbePositions { get; }
    public RhiBuffer GridToProbeIndex { get; }
    public RhiBuffer ProbeWorldKeys { get; }
    public RhiBuffer WorldProbeHash { get; }
    public RhiBuffer ProbeCounter { get; }
    public RhiBuffer ProbeDrawArgs { get; }
    public RhiBuffer ProbeStates { get; }
    public RhiBuffer ProbeUpdateQueue { get; }
    public RhiBuffer VolumeState { get; }
    public RhiBuffer ProbeRequests => _probeRequests[_requestBufferIndex];
    public uint IrradianceBindlessIndex { get; }
    public uint VisibilityBindlessIndex { get; }
    public Vector3I GridResolution { get; }
    public Vector3 Origin { get; }
    public Vector3 Extent { get; }
    public Vector3 BaseCellSize { get; }
    public int ClipmapLevelCount { get; }
    public float ClipmapScale { get; }
    public int MaxProbesTotalBudget { get; }
    public int WorldProbeHashCapacity { get; }
    public int CoarseGridCells { get; }
    public RhiBindlessHeap SharedHeap { get; }
    public DDGIAtlasResourceHandles ResourceHandles { get; }
    internal int ScheduledProbeCapacity { get; set; }
    internal int RequestCount => WorldCache.RequestCount;
    internal int AllocatedProbeCount => WorldCache.AllocatedProbeCount;
    internal bool SceneBakeActive => WorldCache.BakeActive;
    internal bool HasBudgetTrainingWork =>
        WorldCache.SceneBakeRequestCount > 0 ||
        RadianceRefreshActive;
    internal bool RadianceRefreshActive =>
        _radianceRefreshProbeBudget > 0;
    internal DDGIWorldProbeCache WorldCache { get; }
    private readonly RhiBuffer[] _probeRequests = new RhiBuffer[3];
    private int _requestBufferIndex;
    private bool _hasObservedRadianceRevision;
    private uint _observedRadianceRevision;
    private int _radianceRefreshProbeBudget;
    private int _persistentScanCursor;

    internal void TrackRadianceRevision(
        uint radianceRevision,
        int allocatedProbeCount)
    {
        if (_hasObservedRadianceRevision &&
            _observedRadianceRevision == radianceRevision)
        {
            return;
        }
        _hasObservedRadianceRevision = true;
        _observedRadianceRevision = radianceRevision;
        _radianceRefreshProbeBudget = Math.Max(
            DDGIProbeUpdatePass.MaxProbesPerFrame,
            checked(allocatedProbeCount * 2));
    }

    internal int GetPersistentScanStart()
        => AllocatedProbeCount == 0
            ? 0
            : _persistentScanCursor % AllocatedProbeCount;

    internal void AdvancePersistentScan(int scannedProbeCount)
    {
        if (AllocatedProbeCount == 0 || scannedProbeCount <= 0)
            return;
        _persistentScanCursor =
            (_persistentScanCursor + scannedProbeCount) %
            AllocatedProbeCount;
    }

    internal static uint PackRadianceRevision(
        uint lightRevision,
        uint skyRevision)
        => (lightRevision & 0xFFFFu) |
            ((skyRevision & 0xFFFFu) << 16);

    internal void ConsumeRadianceRefreshAllowance(int admittedProbeCount)
    {
        if (admittedProbeCount <= 0 ||
            _radianceRefreshProbeBudget <= 0)
        {
            return;
        }
        _radianceRefreshProbeBudget = Math.Max(
            0,
            _radianceRefreshProbeBudget - admittedProbeCount);
    }

    public DDGIAtlasResources(
        RhiDevice device,
        RhiBindlessHeap sharedHeap,
        Vector3I baseGridResolution,
        Vector3 origin,
        Vector3 extent,
        Vector3 baseCellSize,
        int clipmapLevelCount,
        float clipmapScale,
        int maxProbesTotalBudget)
    {
        if (baseGridResolution.X <= 0 || baseGridResolution.Y <= 0 ||
            baseGridResolution.Z <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseGridResolution),
                "BaseGridResolution components must all be positive.");
        }
        if (maxProbesTotalBudget <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxProbesTotalBudget),
                "maxProbesTotalBudget must be positive.");

        SharedHeap = sharedHeap;
        ResourceHandles = new DDGIAtlasResourceHandles(
            new ResourceHandle(NextGraphResourceId()),
            new ResourceHandle(NextGraphResourceId()),
            new ResourceHandle(NextGraphResourceId()),
            new ResourceHandle(NextGraphResourceId()),
            new ResourceHandle(NextGraphResourceId()),
            new ResourceHandle(NextGraphResourceId()),
            new ResourceHandle(NextGraphResourceId()),
            new ResourceHandle(NextGraphResourceId()),
            new ResourceHandle(NextGraphResourceId()),
            new ResourceHandle(NextGraphResourceId()),
            new ResourceHandle(NextGraphResourceId()));
        GridResolution = baseGridResolution;
        Origin = origin;
        Extent = extent;
        BaseCellSize = baseCellSize;
        ClipmapLevelCount = clipmapLevelCount;
        ClipmapScale = clipmapScale;
        MaxProbesTotalBudget = maxProbesTotalBudget;
        WorldProbeHashCapacity = CalculateHashCapacity(
            maxProbesTotalBudget);
        CoarseGridCells =
            baseGridResolution.X * baseGridResolution.Y *
            baseGridResolution.Z * clipmapLevelCount;
        if (CoarseGridCells > maxProbesTotalBudget)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxProbesTotalBudget),
                "Probe budget must cover every clipmap cell.");
        }

        int irradianceTexelCount = checked(maxProbesTotalBudget * 4);
        Irradiance = RhiTexture.CreateStorage(
            device,
            AtlasWidth,
            (uint)Math.Max(
                1,
                (irradianceTexelCount + AtlasWidth - 1) / AtlasWidth),
            RhiNative.TextureFormat.Rgba16Float);
        Irradiance.SetDebugName("DDGI Irradiance Atlas", "DDGI");

        Visibility = RhiTexture.CreateStorage(
            device,
            AtlasWidth,
            (uint)Math.Max(
                1,
                (maxProbesTotalBudget * VisibilityTexelsPerProbe +
                    AtlasWidth - 1) / AtlasWidth),
            RhiNative.TextureFormat.Rgba16Float);
        Visibility.SetDebugName("DDGI Visibility Atlas", "DDGI");

        ProbePositions = RhiBuffer.Create(
            device,
            (ulong)maxProbesTotalBudget * 16ul,
            RhiNative.BufferUsage.Storage | RhiNative.BufferUsage.Vertex);
        ProbePositions.SetDebugName("DDGI Probe Positions", "DDGI");

        GridToProbeIndex = RhiBuffer.Create(
            device,
            (ulong)CoarseGridCells * sizeof(int),
            RhiNative.BufferUsage.Storage);
        GridToProbeIndex.SetDebugName(
            "DDGI Coarse Grid → Sparse Probe Index", "DDGI");

        ProbeWorldKeys = RhiBuffer.Create(
            device,
            (ulong)maxProbesTotalBudget * 16ul,
            RhiNative.BufferUsage.Storage);
        ProbeWorldKeys.SetDebugName(
            "DDGI Persistent World Probe Keys", "DDGI");

        WorldProbeHash = RhiBuffer.Create(
            device,
            (ulong)WorldProbeHashCapacity * sizeof(uint),
            RhiNative.BufferUsage.Storage);
        WorldProbeHash.SetDebugName(
            "DDGI Persistent World Probe Hash", "DDGI");
        WorldProbeHash.Upload(
            new uint[WorldProbeHashCapacity]);

        ProbeCounter = RhiBuffer.Create(
            device,
            16ul,
            RhiNative.BufferUsage.Storage);
        ProbeCounter.SetDebugName(
            "DDGI Placement Probe Counter", "DDGI");

        ProbeDrawArgs = RhiBuffer.Create(
            device,
            16ul,
            RhiNative.BufferUsage.Storage | RhiNative.BufferUsage.Indirect);
        ProbeDrawArgs.SetDebugName(
            "DDGI GPU Probe Draw Args", "DDGI");

        ProbeStates = RhiBuffer.Create(
            device,
            (ulong)maxProbesTotalBudget * 16ul,
            RhiNative.BufferUsage.Storage);
        ProbeStates.SetDebugName("DDGI GPU Probe States", "DDGI");

        ProbeUpdateQueue = RhiBuffer.Create(
            device,
            (ulong)DDGIProbeUpdatePass.MaxProbesPerFrame * sizeof(uint),
            RhiNative.BufferUsage.Storage);
        ProbeUpdateQueue.SetDebugName("DDGI GPU Update Queue", "DDGI");

        VolumeState = RhiBuffer.Create(
            device,
            128ul,
            RhiNative.BufferUsage.Storage);
        VolumeState.SetDebugName("DDGI Scrolling Volume State", "DDGI");

        int requestCapacity = checked(
            CoarseGridCells +
            DDGIWorldProbeCache.MaxSceneBakeRequestsPerFrame);
        for (int index = 0; index < _probeRequests.Length; ++index)
        {
            _probeRequests[index] = RhiBuffer.Create(
                device,
                (ulong)requestCapacity *
                    (ulong)Marshal.SizeOf<DDGIProbeRequest>(),
                RhiNative.BufferUsage.Storage);
            _probeRequests[index].SetDebugName(
                $"DDGI Probe Requests [{index}]",
                "DDGI");
        }

        WorldCache = new DDGIWorldProbeCache(
            maxProbesTotalBudget,
            baseGridResolution.X,
            clipmapLevelCount,
            baseCellSize,
            clipmapScale);

        // Sentinel value used when the shared heap is null -
        // shader-side consumers gate on the same uint.MaxValue
        // literal (see ddgi_sampling.slang + ddgi_debug.slang)
        // so a failed registration can't be confused with a valid
        // slot-0 atlas binding.
        if (sharedHeap == null)
        {
            Engine.CBindings.Log.Error(
                "[DDGI] atlas allocation received a null bindless heap; " +
                "DDGI atlas slots will be RhiBindlessHeap.InvalidSlot " +
                "(0xFFFFFFFF) so consumers fall through to no-atlas sampling.",
                "DDGI");
            IrradianceBindlessIndex = RhiBindlessHeap.InvalidSlot;
            VisibilityBindlessIndex = RhiBindlessHeap.InvalidSlot;
        }
        else
        {
            IrradianceBindlessIndex = sharedHeap.Register(Irradiance);
            VisibilityBindlessIndex = sharedHeap.Register(Visibility);
        }

    }

    public void Dispose()
    {
        if (SharedHeap != null)
        {
            SharedHeap.Release(IrradianceBindlessIndex);
            SharedHeap.Release(VisibilityBindlessIndex);
        }
        Irradiance?.Dispose();
        Visibility?.Dispose();
        ProbePositions?.Dispose();
        GridToProbeIndex?.Dispose();
        ProbeWorldKeys?.Dispose();
        WorldProbeHash?.Dispose();
        ProbeCounter?.Dispose();
        ProbeDrawArgs?.Dispose();
        ProbeStates?.Dispose();
        ProbeUpdateQueue?.Dispose();
        VolumeState?.Dispose();
        foreach (RhiBuffer requestBuffer in _probeRequests)
            requestBuffer?.Dispose();
    }

    private static uint NextGraphResourceId()
        => unchecked((uint)Interlocked.Increment(ref _nextGraphResourceId));

    private static int CalculateHashCapacity(int probeCapacity)
    {
        int target = checked(probeCapacity * 2);
        int capacity = 1;
        while (capacity < target)
            capacity = checked(capacity << 1);
        return capacity;
    }

    internal void SelectRequestBuffer(long frameNumber)
        => _requestBufferIndex = (int)(
            (ulong)frameNumber % (ulong)_probeRequests.Length);

}

public sealed record Vector3I(int X, int Y, int Z)
{
    public int Volume => X * Y * Z;
}
