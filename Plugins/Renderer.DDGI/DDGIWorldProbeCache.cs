// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Engine.DDGI;

[StructLayout(LayoutKind.Sequential)]
internal struct DDGIProbeRequest
{
    public Vector4 WorldPosition;
    public uint ProbeSlot;
    public uint GridCellIndex;
    public uint ClipmapLevel;
    public uint Flags;
}

internal sealed class DDGIWorldProbeCache
{
    internal const uint InvalidGridCell = uint.MaxValue;
    internal const uint NewAllocationFlag = 1u;
    internal const uint SceneBakeFlag = 2u;
    internal const int MinimumSceneBakeRequestsPerFrame = 1;
    internal const int DefaultSceneBakeRequestsPerFrame = 16;
    internal const int MaxSceneBakeRequestsPerFrame = 64;

    private readonly record struct ProbeKey(
        int X,
        int Y,
        int Z,
        int Level);

    private readonly Dictionary<ProbeKey, int> _slots;
    private readonly bool[] _requestedSlots;
    private readonly int[] _requestedSlotList;
    private readonly DDGIProbeRequest[] _requests;
    private readonly int _capacity;
    private readonly int _gridResolution;
    private readonly int _clipmapLevelCount;
    private readonly Vector3 _baseCellSize;
    private readonly float _clipmapScale;
    private int _allocatedProbeCount;
    private int _requestCount;
    private int _requestedSlotCount;
    private int _sceneBakeRequestCount;
    private uint _bakeGeometryRevision;
    private bool _bakeActive;
    private int _bakeLevel;
    private int _bakeX;
    private int _bakeY;
    private int _bakeZ;
    private int _bakeMinX;
    private int _bakeMinY;
    private int _bakeMinZ;
    private int _bakeMaxX;
    private int _bakeMaxY;
    private int _bakeMaxZ;
    private Vector3 _bakeBoundsMin;
    private Vector3 _bakeBoundsMax;

    internal DDGIWorldProbeCache(
        int capacity,
        int gridResolution,
        int clipmapLevelCount,
        Vector3 baseCellSize,
        float clipmapScale)
    {
        _capacity = capacity;
        _gridResolution = gridResolution;
        _clipmapLevelCount = clipmapLevelCount;
        _baseCellSize = baseCellSize;
        _clipmapScale = clipmapScale;
        int activeCellCount = checked(
            gridResolution * gridResolution * gridResolution *
            clipmapLevelCount);
        _requests = new DDGIProbeRequest[
            activeCellCount + MaxSceneBakeRequestsPerFrame];
        _requestedSlots = new bool[capacity];
        _requestedSlotList = new int[_requests.Length];
        _slots = new Dictionary<ProbeKey, int>(capacity);
    }

    internal int AllocatedProbeCount => _allocatedProbeCount;
    internal int RequestCount => _requestCount;
    internal bool BakeActive => _bakeActive;
    internal int SceneBakeRequestCount => _sceneBakeRequestCount;
    internal ReadOnlySpan<DDGIProbeRequest> Requests =>
        _requests.AsSpan(0, _requestCount);

    internal void PrepareFrame(
        Vector3 cameraPosition,
        uint geometryRevision,
        bool hasSceneBounds,
        Vector3 sceneBoundsMin,
        Vector3 sceneBoundsMax,
        int sceneBakeRequestBudget = DefaultSceneBakeRequestsPerFrame,
        bool canClassifySceneBake = true)
    {
        ResetRequests();
        AddActiveClipmaps(cameraPosition);
        if (hasSceneBounds &&
            (geometryRevision != _bakeGeometryRevision ||
             sceneBoundsMin != _bakeBoundsMin ||
             sceneBoundsMax != _bakeBoundsMax))
        {
            BeginSceneBake(
                geometryRevision,
                sceneBoundsMin,
                sceneBoundsMax);
        }
        if (canClassifySceneBake && sceneBakeRequestBudget > 0)
        {
            AddSceneBakeBatch(Math.Clamp(
                sceneBakeRequestBudget,
                MinimumSceneBakeRequestsPerFrame,
                MaxSceneBakeRequestsPerFrame));
        }
    }

    private void ResetRequests()
    {
        for (int index = 0; index < _requestedSlotCount; ++index)
            _requestedSlots[_requestedSlotList[index]] = false;
        _requestCount = 0;
        _requestedSlotCount = 0;
        _sceneBakeRequestCount = 0;
    }

    private void AddActiveClipmaps(Vector3 cameraPosition)
    {
        int cellsPerLevel = checked(
            _gridResolution * _gridResolution * _gridResolution);
        for (int level = 0; level < _clipmapLevelCount; ++level)
        {
            Vector3 cellSize = CellSize(level);
            Vector3 snappedCenter = new(
                MathF.Floor(cameraPosition.X / cellSize.X) * cellSize.X,
                MathF.Floor(cameraPosition.Y / cellSize.Y) * cellSize.Y,
                MathF.Floor(cameraPosition.Z / cellSize.Z) * cellSize.Z);
            Vector3 volumeMin = snappedCenter -
                cellSize * _gridResolution * 0.5f;
            for (int z = 0; z < _gridResolution; ++z)
                for (int y = 0; y < _gridResolution; ++y)
                    for (int x = 0; x < _gridResolution; ++x)
                    {
                        Vector3 position = volumeMin +
                            new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) *
                            cellSize;
                        ProbeKey key = KeyFromPosition(position, cellSize, level);
                        int slot = GetOrAllocate(key, out bool newAllocation);
                        if (slot < 0)
                            continue;
                        uint localCellIndex = (uint)(
                            z * _gridResolution * _gridResolution +
                            y * _gridResolution + x);
                        AddRequest(
                            position,
                            slot,
                            (uint)(level * cellsPerLevel) + localCellIndex,
                            level,
                            newAllocation ? NewAllocationFlag : 0u);
                    }
        }
    }

    private void BeginSceneBake(
        uint geometryRevision,
        Vector3 boundsMin,
        Vector3 boundsMax)
    {
        _bakeGeometryRevision = geometryRevision;
        _bakeBoundsMin = boundsMin;
        _bakeBoundsMax = boundsMax;
        _bakeLevel = _clipmapLevelCount - 1;
        _bakeActive = true;
        BeginBakeLevel();
    }

    private void BeginBakeLevel()
    {
        Vector3 cellSize = CellSize(_bakeLevel);
        Vector3 padding = cellSize;
        Vector3 minimum = (_bakeBoundsMin - padding) / cellSize;
        Vector3 maximum = (_bakeBoundsMax + padding) / cellSize;
        _bakeMinX = (int)MathF.Floor(minimum.X);
        _bakeMinY = (int)MathF.Floor(minimum.Y);
        _bakeMinZ = (int)MathF.Floor(minimum.Z);
        _bakeMaxX = (int)MathF.Floor(maximum.X);
        _bakeMaxY = (int)MathF.Floor(maximum.Y);
        _bakeMaxZ = (int)MathF.Floor(maximum.Z);
        _bakeX = _bakeMinX;
        _bakeY = _bakeMinY;
        _bakeZ = _bakeMinZ;
    }

    private void AddSceneBakeBatch(int requestBudget)
    {
        int added = 0;
        int examined = 0;
        int examinationBudget = requestBudget * 16;
        while (_bakeActive &&
               added < requestBudget &&
               examined++ < examinationBudget)
        {
            ProbeKey key = new(_bakeX, _bakeY, _bakeZ, _bakeLevel);
            Vector3 cellSize = CellSize(_bakeLevel);
            Vector3 position = new(
                _bakeX * cellSize.X,
                _bakeY * cellSize.Y,
                _bakeZ * cellSize.Z);
            int slot = GetOrAllocate(key, out bool newAllocation);
            AdvanceBakeCursor();
            if (slot < 0 || _requestedSlots[slot])
                continue;
            AddRequest(
                position,
                slot,
                InvalidGridCell,
                _bakeLevel,
                SceneBakeFlag |
                    (newAllocation ? NewAllocationFlag : 0u));
            ++added;
            ++_sceneBakeRequestCount;
        }
    }

    private void AdvanceBakeCursor()
    {
        if (++_bakeX <= _bakeMaxX)
            return;
        _bakeX = _bakeMinX;
        if (++_bakeY <= _bakeMaxY)
            return;
        _bakeY = _bakeMinY;
        if (++_bakeZ <= _bakeMaxZ)
            return;
        if (--_bakeLevel < 0)
        {
            _bakeActive = false;
            return;
        }
        BeginBakeLevel();
    }

    private int GetOrAllocate(ProbeKey key, out bool newAllocation)
    {
        if (_slots.TryGetValue(key, out int slot))
        {
            newAllocation = false;
            return slot;
        }
        if (_allocatedProbeCount >= _capacity)
        {
            newAllocation = false;
            return -1;
        }
        slot = _allocatedProbeCount++;
        _slots.Add(key, slot);
        newAllocation = true;
        return slot;
    }

    private void AddRequest(
        Vector3 position,
        int slot,
        uint gridCellIndex,
        int level,
        uint flags)
    {
        if (_requestCount >= _requests.Length)
            return;
        _requests[_requestCount++] = new DDGIProbeRequest
        {
            WorldPosition = new Vector4(position, 1.0f),
            ProbeSlot = (uint)slot,
            GridCellIndex = gridCellIndex,
            ClipmapLevel = (uint)level,
            Flags = flags
        };
        _requestedSlots[slot] = true;
        _requestedSlotList[_requestedSlotCount++] = slot;
    }

    private ProbeKey KeyFromPosition(
        Vector3 position,
        Vector3 cellSize,
        int level)
        => new(
            (int)MathF.Floor(position.X / cellSize.X),
            (int)MathF.Floor(position.Y / cellSize.Y),
            (int)MathF.Floor(position.Z / cellSize.Z),
            level);

    private Vector3 CellSize(int level)
        => _baseCellSize * MathF.Pow(_clipmapScale, level);
}
