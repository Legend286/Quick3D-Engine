// SPDX-License-Identifier: MIT

using System;

namespace Engine.RenderGraph;

/// <summary>
/// Per-frame GPU work domains admitted by <see cref="GpuWorkScheduler"/>.
/// Add new domains BEFORE <see cref="Count"/> so the
/// <c>_domains[(int)domain]</c> indexing stays stable across reloads.
/// </summary>
public enum GpuWorkDomain
{
    Shadows,
    PunctualShadows,
    BackgroundCompute,
    /// <summary>Dynamic Diffuse Global Illumination — probe
    /// gather / SH projection / atlas update. Uses a measured 4 ms
    /// budget with a 128-probe hard ceiling.</summary>
    Gi,
    Count,
}

/// <summary>Provides frame-indexed completed unit counts for GPU timing.</summary>
public interface IGpuWorkTimingSource
{
    /// <summary>Gets the scheduler domain measured by this pass.</summary>
    GpuWorkDomain WorkDomain { get; }

    /// <summary>Gets the units submitted in a previously captured frame.</summary>
    bool TryGetSubmittedUnitCount(long frameNumber, out int unitCount);
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
        public int BurstMaximumUnits;
        public double CarryLimitMilliseconds;
        public double CarryMilliseconds;
        public double AvailableMilliseconds;
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
            BurstMaximumUnits = 1,
        },
        new()
        {
            Name = "Punctual Shadows",
            BudgetMilliseconds = 6.0,
            EstimatedUnitMilliseconds = 0.25,
            MaximumUnits = 24,
            BurstMaximumUnits = 48,
        },
        new()
        {
            Name = "Background Compute",
            BudgetMilliseconds = 1.5,
            EstimatedUnitMilliseconds = 0.25,
            MaximumUnits = 4,
            BurstMaximumUnits = 4,
        },
        new()
        {
            Name = "Global Illumination",
            BudgetMilliseconds = 4.0,
            EstimatedUnitMilliseconds = 0.125,
            MaximumUnits = 128,
            BurstMaximumUnits = 128,
        },
    };

    private long _frameNumber = -1;
    private int _punctualHeadroomSamples;
    private int _punctualPressureSamples;

    public void BeginFrame(long frameNumber)
    {
        if (_frameNumber == frameNumber)
            return;

        foreach (DomainState domain in _domains)
        {
            if (_frameNumber >= 0 && domain.CarryLimitMilliseconds > 0.0)
            {
                domain.CarryMilliseconds = Math.Min(
                    Math.Max(
                        domain.AvailableMilliseconds -
                            domain.AdmittedMilliseconds,
                        0.0),
                    domain.CarryLimitMilliseconds);
            }
            domain.AvailableMilliseconds =
                domain.BudgetMilliseconds + domain.CarryMilliseconds;
            domain.AdmittedUnits = 0;
            domain.DeferredUnits = 0;
            domain.AdmittedMilliseconds = 0.0;
        }
        _frameNumber = frameNumber;
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
        int frameMaximumUnits = GetFrameMaximumUnits(state);
        bool withinUnitLimit =
            state.AdmittedUnits + unitCount <= frameMaximumUnits;
        bool withinTimeLimit =
            state.AdmittedUnits == 0 ||
            state.AdmittedMilliseconds +
                state.EstimatedUnitMilliseconds * unitCount <=
                    GetAvailableMilliseconds(state);
        if (!forced && (!withinUnitLimit || !withinTimeLimit))
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
            GetAvailableMilliseconds(state) /
            Math.Max(state.EstimatedUnitMilliseconds, 0.001));
        return Math.Clamp(
            Math.Max(timeLimitedUnits, minimumAtomicUnits),
            minimumAtomicUnits,
            GetFrameMaximumUnits(state));
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

        // Cap the maximum estimated per-unit time so we don't completely starve
        // the domain if fixed overhead dominates a small unit count.
        double maxPerUnit = state.BudgetMilliseconds / 4.0;
        measuredUnitMilliseconds = Math.Min(measuredUnitMilliseconds, maxPerUnit);

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
                GetFrameMaximumUnits(state),
                state.AdmittedUnits,
                state.DeferredUnits,
                state.TotalAdmittedUnits,
                state.TotalDeferredUnits);
        }
        return snapshots;
    }

    private static int GetFrameMaximumUnits(DomainState state)
    {
        int carryUnits = (int)Math.Floor(
            state.CarryMilliseconds /
            Math.Max(state.EstimatedUnitMilliseconds, 0.001));
        return Math.Clamp(
            state.MaximumUnits + carryUnits,
            state.MaximumUnits,
            Math.Max(state.BurstMaximumUnits, state.MaximumUnits));
    }

    private static double GetAvailableMilliseconds(DomainState state)
        => state.AvailableMilliseconds > 0.0
            ? state.AvailableMilliseconds
            : state.BudgetMilliseconds + state.CarryMilliseconds;
}
