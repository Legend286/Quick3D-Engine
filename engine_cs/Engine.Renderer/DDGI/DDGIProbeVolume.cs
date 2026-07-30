// SPDX-License-Identifier: MIT
using System;
using System.Numerics;

namespace Engine.Renderer.DDGI;

/// <summary>
/// CPU-side description of a dense DDGI probe volume. The probe grid is
/// uniform and axis-aligned; per-probe positions are derived from
/// <see cref="GridResolution"/> and the world-space <see cref="Origin"/>
/// + <see cref="Extent"/> AABB. GPU resources (atlas textures + position
/// buffers) are owned by <see cref="DDGIProbeUpdatePass"/>; this type
/// stays purely data so it can be inspected in tests without a device.
/// </summary>
/// <remarks>
/// Default convention matches the Iris/SEED-DDGI papers: <see cref="Origin"/>
/// is the AABB minimum corner, <see cref="Extent"/> is half-extent (the
/// AABB is symmetric around an internal center). A 16x16x16 grid with
/// 16-meter half-extent gives 4096 probes at 1-meter spacing, which fits
/// the 2ms-per-frame budget with octahedral 32-ray sampling when
/// staggered across multiple frames.
/// </remarks>
public sealed class DDGIProbeVolume
{
    public Vector3 Origin { get; }
    public Vector3 Extent { get; }
    public int GridResolution { get; }

    public int ProbeCount => GridResolution * GridResolution * GridResolution;

    public Vector3 CellSize =>
        GridResolution > 0
            ? Extent * 2.0f / GridResolution
            : Vector3.Zero;

    public DDGIProbeVolume(Vector3 origin, Vector3 extent, int gridResolution)
    {
        if (gridResolution <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(gridResolution),
                "GridResolution must be positive (e.g. 16 for the canonical DDGI volume).");
        if (extent.X <= 0 || extent.Y <= 0 || extent.Z <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(extent),
                "Extent components must all be positive (half-extents, in meters).");

        Origin = origin;
        Extent = extent;
        GridResolution = gridResolution;
    }

    /// <summary>Computes the world-space center of probe <paramref name="index"/>.</summary>
    public Vector3 PositionAt(int index)
    {
        if (index < 0 || index >= ProbeCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        int linearZ = index / (GridResolution * GridResolution);
        int linearY = (index / GridResolution) % GridResolution;
        int linearX = index % GridResolution;

        Vector3 fractional = new(
            (linearX + 0.5f) / GridResolution,
            (linearY + 0.5f) / GridResolution,
            (linearZ + 0.5f) / GridResolution);
        return Origin - Extent + fractional * (Extent * 2.0f);
    }

    /// <summary>
    /// Maps a world position back to a normalized[-1, 1] grid coordinate.
    /// Returns false when the position falls outside the volume so callers
    /// can fall back to cubes / cubemaps / sky-only contribution.
    /// </summary>
    public bool TryGetGridUVW(Vector3 worldPosition, out Vector3 gridUVW)
    {
        Vector3 fullExtent = Extent * 2.0f;
        Vector3 relative = (worldPosition - Origin + Extent) / fullExtent;
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

    /// <summary>Allocates a flat array sized to the probe count.</summary>
    public T[] AllocateProbeArray<T>() => new T[ProbeCount];
}
