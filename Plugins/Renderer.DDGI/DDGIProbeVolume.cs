// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Engine.DDGI;

/// <summary>
/// CPU-side description of a sparse DDGI probe volume. The volume's
/// outer AABB is uniform + axis-aligned (<see cref="Origin"/> +
/// <see cref="Extent"/>) but the actual probe set lives in a
/// per-instance world position list — only cells whose entry in the
/// <see cref="GridToProbeIndex"/> indirection array indexes a valid
/// sparse probe contribute to indirect-diffuse sampling.
///
/// Layout contract:
///   * <see cref="BaseGridResolution"/> = 32 (constant; one octree
///     level per axis divides the coarse AABB into 32×32×32 = 32768
///     candidate cells). Shader fallback lives at this resolution.
///   * <see cref="MaxProbesTotalBudget"/> = 4096 (placement cap;
///     per-cell indirection table maps accepted sparse positions).
///   * ProbeCount = accepted-probes Count. Decoupled from the coarse
///     grid resolution so sparse layouts route to the atlas's
///     (probeCount * 4) column shape unchanged.
/// </summary>
public sealed class DDGIProbeVolume
{
    public const int DefaultBaseGridResolution = 32;
    public const int DefaultMaxProbesTotalBudget = 4096;

    public Vector3 Origin { get; }
    public Vector3 Extent { get; }
    public int BaseGridResolution { get; }
    public int MaxProbesTotalBudget { get; }
    public int ProbeCount => _positions?.Length ?? 0;
    public Vector3 CellSize =>
        BaseGridResolution > 0
            ? Extent * 2.0f / BaseGridResolution
            : Vector3.Zero;

    private Vector3[]? _positions;
    private int[]? _gridToProbeIndex;
    private bool _initialized;

    public DDGIProbeVolume(
        Vector3 origin,
        Vector3 extent,
        int gridResolution = DefaultBaseGridResolution,
        int maxProbesTotalBudget = DefaultMaxProbesTotalBudget)
    {
        if (gridResolution <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(gridResolution),
                "BaseGridResolution must be positive (e.g. 32 for the sparse DDGI volume).");
        if (extent.X <= 0 || extent.Y <= 0 || extent.Z <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(extent),
                "Extent components must all be positive (half-extents, in meters).");
        if (maxProbesTotalBudget <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(maxProbesTotalBudget),
                "MaxProbesTotalBudget must be positive.");

        Origin = origin;
        Extent = extent;
        BaseGridResolution = gridResolution;
        MaxProbesTotalBudget = maxProbesTotalBudget;
    }

    /// <summary>Initialises the sparse layout from an approved
    /// position list. <paramref name="gridToProbeIndex"/> length must
    /// equal <c>BaseGridResolution³</c>; sentinel value
    /// <c>-1</c> marks a coarse cell with no accepted probe.</summary>
    public void Initialize(
        IReadOnlyList<Vector3> positions,
        int[] gridToProbeIndex)
    {
        if (positions == null)
            throw new ArgumentNullException(nameof(positions));
        if (gridToProbeIndex == null)
            throw new ArgumentNullException(nameof(gridToProbeIndex));
        int expected =
            BaseGridResolution * BaseGridResolution * BaseGridResolution;
        if (gridToProbeIndex.Length != expected)
            throw new ArgumentException(
                $"gridToProbeIndex.Length must equal " +
                $"BaseGridResolution³ = {expected}.",
                nameof(gridToProbeIndex));
        if (positions.Count > MaxProbesTotalBudget)
            throw new ArgumentException(
                $"positions.Count {positions.Count} exceeds " +
                $"MaxProbesTotalBudget {MaxProbesTotalBudget}.",
                nameof(positions));

        _positions = new Vector3[positions.Count];
        for (int i = 0; i < positions.Count; ++i)
            _positions[i] = positions[i];
        _gridToProbeIndex = (int[])gridToProbeIndex.Clone();
        _initialized = true;
    }

    /// <summary>Marks the volume as GPU-owned. The CPU retains only the
    /// volume bounds; probe positions and indirection are generated and
    /// updated by the placement kernel.</summary>
    public void InitializeGpuOwned()
    {
        _positions = Array.Empty<Vector3>();
        _gridToProbeIndex = AllocateGridIndirection();
        _initialized = true;
    }

    /// <summary>True once the volume metadata is ready. Probe positions
    /// are GPU-owned when <see cref="InitializeGpuOwned"/> was used.</summary>
    public bool IsInitialized => _initialized;

    /// <summary>World-space position of probe index
    /// <paramref name="index"/> in the sparse list; throws before
    /// <see cref="Initialize"/>.</summary>
    public Vector3 PositionAt(int index)
    {
        EnsureInitialized();
        if (index < 0 || index >= _positions!.Length)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _positions[index];
    }

    /// <summary>Coarse-grid cell index → sparse probe atlas index,
    /// or -1 if the coarse cell has no accepted probe.</summary>
    public int GetSparseProbeIndex(int3 cell)
    {
        EnsureInitialized();
        if (cell.X < 0 || cell.X >= BaseGridResolution ||
            cell.Y < 0 || cell.Y >= BaseGridResolution ||
            cell.Z < 0 || cell.Z >= BaseGridResolution)
            return -1;
        int linear =
            cell.Z * BaseGridResolution * BaseGridResolution +
            cell.Y * BaseGridResolution +
            cell.X;
        return _gridToProbeIndex![linear];
    }

    /// <summary>Maps a world position back to a normalized[0,1]
    /// grid coordinate. Returns false when the position falls
    /// outside the volume, in which case callers should fall back
    /// to zero contribution.</summary>
    public bool TryGetGridUVW(Vector3 worldPosition, out Vector3 gridUVW)
    {
        Vector3 fullExtent = Extent * 2.0f;
        Vector3 relative =
            (worldPosition - Origin + Extent) / fullExtent;
        if (relative.X < 0.0f || relative.X > 1.0f ||
            relative.Y < 0.0f || relative.Y > 1.0f ||
            relative.Z < 0.0f || relative.Z > 1.0f)
        {
            gridUVW = Vector3.Zero;
            return false;
        }
        gridUVW = relative;
        return true;
    }

    /// <summary>Allocates the indirection array sized to the
    /// coarse-grid volume, default-sentinelised to <c>-1</c>.
    /// Used by the GPU placement pass to seed the SSBO before
    /// atomically filling accepted cells.</summary>
    public int[] AllocateGridIndirection(int sentinel = -1)
    {
        int length =
            BaseGridResolution * BaseGridResolution * BaseGridResolution;
        var grid = new int[length];
        Array.Fill(grid, sentinel);
        return grid;
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException(
                "DDGIProbeVolume.Initialize must be called before " +
                "querying sparse positions.");
    }
}

/// <summary>3-component int cell coordinate for the DDGI coarse
/// grid. Kept as a value type so the C# `% 32 / 32 / 32` slicing
/// pattern fits in registers when reading the indirection array.</summary>
public readonly record struct int3(int X, int Y, int Z);
