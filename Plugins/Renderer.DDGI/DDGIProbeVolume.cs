// SPDX-License-Identifier: MIT
using System;
using System.Numerics;

namespace Engine.DDGI;

/// <summary>Configures finite GPU residency for a world-unbounded DDGI cache.</summary>
public sealed class DDGIProbeVolume
{
    public const int DefaultBaseGridResolution = 11;
    public const int DefaultClipmapLevelCount = 3;
    public const float DefaultClipmapScale = 4.0f;
    public const int DefaultMaxProbesTotalBudget = 262144;

    public Vector3 Origin { get; }
    public Vector3 Extent { get; }
    public Vector3 BaseCellSize { get; }
    public int BaseGridResolution { get; }
    public int ClipmapLevelCount { get; }
    public float ClipmapScale { get; }
    public int MaxProbesTotalBudget { get; }
    public Vector3 CellSize => BaseCellSize;

    public DDGIProbeVolume(
        Vector3 origin,
        Vector3 baseCellSize,
        int gridResolution = DefaultBaseGridResolution,
        int clipmapLevelCount = DefaultClipmapLevelCount,
        float clipmapScale = DefaultClipmapScale,
        int maxProbesTotalBudget = DefaultMaxProbesTotalBudget)
    {
        if (gridResolution <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(gridResolution));
        if (baseCellSize.X <= 0 ||
            baseCellSize.Y <= 0 ||
            baseCellSize.Z <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseCellSize));
        }
        if (clipmapLevelCount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(clipmapLevelCount));
        if (clipmapScale <= 1.0f)
            throw new ArgumentOutOfRangeException(
                nameof(clipmapScale));
        int requiredSlots =
            gridResolution *
            gridResolution *
            gridResolution *
            clipmapLevelCount;
        if (maxProbesTotalBudget < requiredSlots)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxProbesTotalBudget));
        }

        Origin = origin;
        BaseCellSize = baseCellSize;
        BaseGridResolution = gridResolution;
        ClipmapLevelCount = clipmapLevelCount;
        ClipmapScale = clipmapScale;
        float farScale = MathF.Pow(
            clipmapScale,
            clipmapLevelCount - 1);
        Extent = baseCellSize * gridResolution * 0.5f * farScale;
        MaxProbesTotalBudget = maxProbesTotalBudget;
    }

}
