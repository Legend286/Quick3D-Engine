// SPDX-License-Identifier: MIT

using System;

namespace Engine.Game;

internal enum GpuWorkDomain
{
    Shadows,
    PunctualShadows,
    BackgroundCompute,
    Count,
}

internal readonly record struct GpuWorkBudgetSnapshot(
    string Name,
    double BudgetMilliseconds,
    double EstimatedUnitMilliseconds,
    int AdmittedUnits,
    int DeferredUnits,
    long TotalAdmittedUnits,
    long TotalDeferredUnits);

internal sealed class GpuWorkScheduler
{
    private sealed class DomainState
    {
        public required string Name;
        public double BudgetMilliseconds;
        public double EstimatedUnitMilliseconds;
        public int MaximumUnits;
        public int AdmittedUnits;
        public int DeferredUnits;
        public long TotalAdmittedUnits;
        public long TotalDeferredUnits;
        public double AdmittedMilliseconds;
    }

    private readonly DomainState[] _domains =
    {
        new()
        {
            Name = "Shadows",
            BudgetMilliseconds = 2.0,
            EstimatedUnitMilliseconds = 1.0,
            MaximumUnits = 1,
        },
        new()
        {
            Name = "Punctual Shadows",
            BudgetMilliseconds = 6.0,
            EstimatedUnitMilliseconds = 0.3,
            MaximumUnits = 20,
        },
        new()
        {
            Name = "Background Compute",
            BudgetMilliseconds = 1.5,
            EstimatedUnitMilliseconds = 0.25,
            MaximumUnits = 4,
        },
    };

    private long _frameNumber = -1;

    public void BeginFrame(long frameNumber)
    {
        if (_frameNumber == frameNumber)
            return;

        _frameNumber = frameNumber;
        foreach (DomainState domain in _domains)
        {
            domain.AdmittedUnits = 0;
            domain.DeferredUnits = 0;
            domain.AdmittedMilliseconds = 0.0;
        }
    }

    public bool TryAdmit(GpuWorkDomain domain, bool forced = false)
        => TryAdmit(domain, 1, forced);

    public bool TryAdmit(
        GpuWorkDomain domain,
        int unitCount,
        bool forced = false)
    {
        if (unitCount <= 0)
            return true;

        DomainState state = _domains[(int)domain];
        bool withinUnitLimit =
            state.AdmittedUnits + unitCount <= state.MaximumUnits;
        bool withinTimeLimit =
            state.AdmittedUnits == 0 ||
            state.AdmittedMilliseconds +
                state.EstimatedUnitMilliseconds * unitCount <=
                    state.BudgetMilliseconds;
        if (!withinUnitLimit || (!forced && !withinTimeLimit))
        {
            state.DeferredUnits += unitCount;
            state.TotalDeferredUnits += unitCount;
            return false;
        }

        state.AdmittedUnits += unitCount;
        state.TotalAdmittedUnits += unitCount;
        state.AdmittedMilliseconds +=
            state.EstimatedUnitMilliseconds * unitCount;
        return true;
    }

    public void Defer(GpuWorkDomain domain, int unitCount)
    {
        if (unitCount <= 0)
            return;
        DomainState state = _domains[(int)domain];
        state.DeferredUnits += unitCount;
        state.TotalDeferredUnits += unitCount;
    }

    public void RecordCompletedWork(
        GpuWorkDomain domain,
        double milliseconds,
        int completedUnits)
    {
        if (completedUnits <= 0 ||
            !double.IsFinite(milliseconds) ||
            milliseconds <= 0.0)
        {
            return;
        }

        DomainState state = _domains[(int)domain];
        double measuredUnitMilliseconds = milliseconds / completedUnits;
        state.EstimatedUnitMilliseconds =
            state.EstimatedUnitMilliseconds * 0.8 +
            measuredUnitMilliseconds * 0.2;
    }

    public GpuWorkBudgetSnapshot[] GetSnapshots()
    {
        var snapshots = new GpuWorkBudgetSnapshot[(int)GpuWorkDomain.Count];
        for (int i = 0; i < snapshots.Length; ++i)
        {
            DomainState state = _domains[i];
            snapshots[i] = new GpuWorkBudgetSnapshot(
                state.Name,
                state.BudgetMilliseconds,
                state.EstimatedUnitMilliseconds,
                state.AdmittedUnits,
                state.DeferredUnits,
                state.TotalAdmittedUnits,
                state.TotalDeferredUnits);
        }
        return snapshots;
    }
}
