// SPDX-License-Identifier: MIT

using System;

namespace Engine.Game;

public enum GpuWorkDomain
{
    Shadows,
    PunctualShadows,
    BackgroundCompute,
    Count,
}

public readonly record struct GpuWorkBudgetSnapshot(
    string Name,
    double BudgetMilliseconds,
    double EstimatedUnitMilliseconds,
    int MaximumUnits,
    int AdmittedUnits,
    int DeferredUnits,
    long TotalAdmittedUnits,
    long TotalDeferredUnits);

public sealed class GpuWorkScheduler
{
    private const double PunctualMinimumBudgetMilliseconds = 6.0;
    private const double PunctualMaximumBudgetMilliseconds = 9.0;
    private const int PunctualMinimumUnits = 24;
    private const int PunctualMaximumUnits = 48;
    private const int PunctualUnitStep = 6;
    private const double PunctualBudgetStepMilliseconds = 0.75;
    private const double FrameHeadroomThresholdMilliseconds = 10.5;
    private const double FramePressureThresholdMilliseconds = 14.0;
    private const int FrameHeadroomSampleCount = 4;
    private const int FramePressureSampleCount = 8;

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
            EstimatedUnitMilliseconds = 0.25,
            MaximumUnits = 24,
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
    private int _punctualHeadroomSamples;
    private int _punctualPressureSamples;

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

    public int GetUnitAllowance(
        GpuWorkDomain domain,
        int minimumAtomicUnits = 1)
    {
        DomainState state = _domains[(int)domain];
        int timeLimitedUnits = (int)Math.Floor(
            state.BudgetMilliseconds /
            Math.Max(state.EstimatedUnitMilliseconds, 0.001));
        return Math.Clamp(
            Math.Max(timeLimitedUnits, minimumAtomicUnits),
            minimumAtomicUnits,
            state.MaximumUnits);
    }

    public void RecordFrameGpuTime(double milliseconds)
    {
        if (!double.IsFinite(milliseconds) || milliseconds <= 0.0)
            return;

        DomainState state =
            _domains[(int)GpuWorkDomain.PunctualShadows];
        if (milliseconds <= FrameHeadroomThresholdMilliseconds)
        {
            _punctualHeadroomSamples++;
            _punctualPressureSamples = 0;
            if (_punctualHeadroomSamples < FrameHeadroomSampleCount)
                return;
            state.MaximumUnits = Math.Min(
                state.MaximumUnits + PunctualUnitStep,
                PunctualMaximumUnits);
            state.BudgetMilliseconds = Math.Min(
                state.BudgetMilliseconds +
                    PunctualBudgetStepMilliseconds,
                PunctualMaximumBudgetMilliseconds);
            _punctualHeadroomSamples = 0;
            return;
        }

        if (milliseconds >= FramePressureThresholdMilliseconds)
        {
            _punctualPressureSamples++;
            _punctualHeadroomSamples = 0;
            if (_punctualPressureSamples < FramePressureSampleCount)
                return;
            state.MaximumUnits = Math.Max(
                state.MaximumUnits - PunctualUnitStep,
                PunctualMinimumUnits);
            state.BudgetMilliseconds = Math.Max(
                state.BudgetMilliseconds -
                    PunctualBudgetStepMilliseconds,
                PunctualMinimumBudgetMilliseconds);
            _punctualPressureSamples = 0;
            return;
        }

        _punctualHeadroomSamples = 0;
        _punctualPressureSamples = 0;
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
                state.MaximumUnits,
                state.AdmittedUnits,
                state.DeferredUnits,
                state.TotalAdmittedUnits,
                state.TotalDeferredUnits);
        }
        return snapshots;
    }
}
