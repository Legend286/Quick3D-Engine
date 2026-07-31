// SPDX-License-Identifier: MIT
using System;
using System.Numerics;
using Engine.CBindings;
using Engine.RHI;

namespace Engine.DDGI;

/// <summary>
/// Phase-3 DDGI atlas asset bundle. Plugin owns the lifetime so the
/// plugin assembly supplies the textures as a self-contained surface
/// that the host assemblies (Engine.Renderer, Engine.RenderGraph)
/// only know about through the contract types declared here.
/// </summary>
/// <remarks>
/// Memory layout (canonical-DDGI-v1 packing, no L2):
///   * <see cref="Irradiance"/> is a 2D RGBA16F atlas of
///     <c>GridResolution^3 * 4</c> texels. Each probe stores 4 SH
///     coefficients (Y00, Y1_-1, Y10, Y11) — one RGBA16F per band —
///     packed row-major at width = GridResolution / 2 * 4, height =
///     GridResolution * 2 etc. so probeIdx -> atlas(uv) is a single
///     divide-and-modulo lookup. A coding pass writes the canonical
///     formula via atomic-add in the dispatch kernel.
///   * <see cref="Visibility"/> is a 2D R16F atlas of
///     <c>GridResolution^3</c> texels carrying the per-probe mean
///     hit distance so the consumer can run a Chebychev
///     backface-test.
/// </remarks>
public sealed class DDGIAtlasResources : IDisposable
{
    public RhiTexture Irradiance { get; }
    public RhiTexture Visibility { get; }
    public RhiBuffer ProbePositions { get; }
    public RhiBuffer GridToProbeIndex { get; }
    public RhiBuffer Lights { get; }
    public RhiBuffer ProbeCounter { get; }
    public uint IrradianceBindlessIndex { get; }
    public uint VisibilityBindlessIndex { get; }
    public Vector3I GridResolution { get; }
    public Vector3 Origin { get; }
    public Vector3 Extent { get; }
    public int MaxProbesTotalBudget { get; }
    public int CoarseGridCells { get; }
    public int UploadedProbeCount { get; private set; }
    public bool SparseLayoutReady { get; private set; }
    public int LightSlotCount =>
        (int)((Lights?.Size ?? 0ul) / 16ul);
    public RhiBindlessHeap SharedHeap { get; }

    public DDGIAtlasResources(
        RhiDevice device,
        RhiBindlessHeap sharedHeap,
        Vector3I baseGridResolution,
        Vector3 origin,
        Vector3 extent,
        uint maxLights,
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
        GridResolution = baseGridResolution;
        Origin = origin;
        Extent = extent;
        MaxProbesTotalBudget = maxProbesTotalBudget;
        CoarseGridCells =
            baseGridResolution.X * baseGridResolution.Y *
            baseGridResolution.Z;

        Irradiance = RhiTexture.CreateStorage(
            device,
            (uint)Math.Max(1, maxProbesTotalBudget * 4),
            1,
            RhiNative.TextureFormat.Rgba16Float);
        Irradiance.SetDebugName("DDGI Irradiance Atlas", "DDGI");

        Visibility = RhiTexture.CreateStorage(
            device,
            (uint)Math.Max(1, maxProbesTotalBudget),
            1,
            // TODO(ddgi): when RhiNative.TextureFormat.R16Float lands in the
        // C-backing enum + Metal MTLPixelFormatR16Float switch case,
        // switch the visibility atlas back to R16F to reclaim the
        // 4x bandwidth/storage penalty RGBA16F pays for an
        // effectively-single-channel visibility score.
        RhiNative.TextureFormat.Rgba16Float);
        Visibility.SetDebugName("DDGI Visibility Atlas", "DDGI");

        ProbePositions = RhiBuffer.Create(
            device,
            (ulong)maxProbesTotalBudget * 12ul,
            RhiNative.BufferUsage.Storage);
        ProbePositions.SetDebugName("DDGI Probe Positions", "DDGI");

        GridToProbeIndex = RhiBuffer.Create(
            device,
            (ulong)CoarseGridCells * sizeof(int),
            RhiNative.BufferUsage.Storage);
        GridToProbeIndex.SetDebugName(
            "DDGI Coarse Grid → Sparse Probe Index", "DDGI");

        ProbeCounter = RhiBuffer.Create(
            device,
            sizeof(uint),
            RhiNative.BufferUsage.Storage);
        ProbeCounter.SetDebugName(
            "DDGI Placement Probe Counter", "DDGI");

        Lights = RhiBuffer.Create(
            device,
            (ulong)maxLights * 16ul /* packed LightData */,
            RhiNative.BufferUsage.Storage);
        Lights.SetDebugName("DDGI Light Snapshot", "DDGI");

        IrradianceBindlessIndex =
            sharedHeap?.Register(Irradiance) ?? 0u;
        VisibilityBindlessIndex =
            sharedHeap?.Register(Visibility) ?? 0u;

        SparseLayoutReady = false;
        UploadedProbeCount = 0;
    }

    /// <summary>
    /// Uploads the sparse layout populated by the GPU placement pass:
    /// world-space per-probe positions + the coarse-grid indirection
    /// array. <paramref name="positions"/> length must be
    /// ≤ <see cref="MaxProbesTotalBudget"/>. Until this is invoked,
    /// <see cref="SparseLayoutReady"/> is false and consumer
    /// shaders should skip DDGI sampling entirely.
    /// </summary>
    public void UploadSparseLayout(
        Vector3[] positions,
        int[] gridToProbeIndex)
    {
        if (positions == null)
            throw new ArgumentNullException(nameof(positions));
        if (gridToProbeIndex == null)
            throw new ArgumentNullException(nameof(gridToProbeIndex));
        if (positions.Length > MaxProbesTotalBudget)
            throw new ArgumentException(
                $"positions.Length {positions.Length} exceeds " +
                $"MaxProbesTotalBudget {MaxProbesTotalBudget}.");
        if (gridToProbeIndex.Length != CoarseGridCells)
            throw new ArgumentException(
                $"gridToProbeIndex.Length {gridToProbeIndex.Length} " +
                $"does not match CoarseGridCells {CoarseGridCells}.",
                nameof(gridToProbeIndex));

        ProbePositions.Upload(new ReadOnlySpan<Vector3>(positions));
        UploadInts(
            GridToProbeIndex,
            new ReadOnlySpan<int>(gridToProbeIndex));
        UploadedProbeCount = positions.Length;
        SparseLayoutReady = true;
    }

    /// <summary>
    /// Resets the placement counter SSBO to zero. Call this from the
    /// placement pass's <c>Execute()</c> BEFORE dispatch so the atomic
    /// <see cref="ddgi_probe_placement.slang"/> kernel starts with a
    /// clean slot allocator. Without this reset, the GPU counter
    /// retains its terminal value from the previous placement run
    /// and atomic adds accumulate against stale offsets in
    /// <see cref="ProbePositions"/>.
    /// </summary>
    public void ZeroProbeCounter()
    {
        uint zero = 0;
        ProbeCounter.Upload(new ReadOnlySpan<uint>(ref zero));
    }

    private static unsafe void UploadInts(RhiBuffer buffer, ReadOnlySpan<int> data)
    {
        fixed (int* p = data)
        {
            buffer.Upload(new ReadOnlySpan<byte>(
                p, data.Length * sizeof(int)));
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
        ProbeCounter?.Dispose();
        Lights?.Dispose();
    }
}

public sealed record Vector3I(int X, int Y, int Z)
{
    public int Volume => X * Y * Z;
}

/// <summary>Snapshot of scene-light state uploaded once per Phase-3
/// dispatch. Packs the same fields as the host's
/// <c>LightData</c> but on the plugin side to avoid host coupling.</summary>
[System.Runtime.InteropServices.StructLayout(
    System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct DDGILightSnapshot
{
    public Vector4 Position;
    public Vector4 Direction;
    public Vector4 Color;
    public Vector4 ShapeParams;
}
