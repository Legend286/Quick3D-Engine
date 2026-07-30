// SPDX-License-Identifier: MIT

using Xunit;

namespace Engine.Game.Tests;

public sealed class GpuWorkSchedulerTests
{
    [Fact]
    public void ShadowBudget_AdmitsAtMostOneUnitPerFrame()
    {
        var scheduler = new GpuWorkScheduler();
        scheduler.BeginFrame(1);

        Assert.True(scheduler.TryAdmit(GpuWorkDomain.Shadows));
        Assert.False(scheduler.TryAdmit(GpuWorkDomain.Shadows));

        GpuWorkBudgetSnapshot shadow = scheduler.GetSnapshots()[0];
        Assert.Equal(1, shadow.AdmittedUnits);
        Assert.Equal(1, shadow.DeferredUnits);
        Assert.Equal(1, shadow.TotalAdmittedUnits);
        Assert.Equal(1, shadow.TotalDeferredUnits);
    }

    [Fact]
    public void ShadowAtlasBudgets_LimitPageCounts()
    {
        Assert.Equal(
            16ul,
            ShadowAtlas.DefaultBudgetBytes / ShadowAtlas.BytesPerPage);
        Assert.Equal(
            24ul,
            ShadowAtlas.HardBudgetBytes / ShadowAtlas.BytesPerPage);
        Assert.Equal(2, ShadowAtlas.FindMinimumSubdivision(1));
        Assert.Equal(4, ShadowAtlas.FindMinimumSubdivision(6));
        Assert.Equal(8, ShadowAtlas.FindMinimumSubdivision(17));
    }

    [Fact]
    public void PunctualShadowBudget_IsIndependentFromDirectionalBudget()
    {
        var scheduler = new GpuWorkScheduler();
        scheduler.BeginFrame(1);

        Assert.True(scheduler.TryAdmit(GpuWorkDomain.Shadows));
        Assert.True(scheduler.TryAdmit(GpuWorkDomain.PunctualShadows));

        GpuWorkBudgetSnapshot[] snapshots = scheduler.GetSnapshots();
        Assert.Equal("Shadows", snapshots[0].Name);
        Assert.Equal("Punctual Shadows", snapshots[1].Name);
        Assert.Equal(6.0, snapshots[1].BudgetMilliseconds);
    }

    [Fact]
    public void PunctualShadowBudget_AdmitsMoreThanSixFaceUpdates()
    {
        var scheduler = new GpuWorkScheduler();
        scheduler.BeginFrame(1);

        for (int pair = 0; pair < 5; pair++)
        {
            Assert.True(
                scheduler.TryAdmit(
                    GpuWorkDomain.PunctualShadows,
                    2));
        }

        Assert.Equal(10, scheduler.GetSnapshots()[1].AdmittedUnits);
    }

    [Fact]
    public void PunctualShadowBudget_AdmitsOneTwentyFourFaceBatch()
    {
        var scheduler = new GpuWorkScheduler();
        scheduler.BeginFrame(1);

        Assert.True(
            scheduler.TryAdmit(
                GpuWorkDomain.PunctualShadows,
                24));
        Assert.False(
            scheduler.TryAdmit(
                GpuWorkDomain.PunctualShadows));

        GpuWorkBudgetSnapshot punctual = scheduler.GetSnapshots()[1];
        Assert.Equal(24, punctual.AdmittedUnits);
        Assert.Equal(1, punctual.DeferredUnits);
    }

    [Fact]
    public void PunctualShadowBudget_GrowsAfterSustainedFrameHeadroom()
    {
        var scheduler = new GpuWorkScheduler();

        for (int sample = 0; sample < 4; ++sample)
            scheduler.RecordFrameGpuTime(9.0);

        GpuWorkBudgetSnapshot punctual = scheduler.GetSnapshots()[1];
        Assert.Equal(30, punctual.MaximumUnits);
        Assert.Equal(6.75, punctual.BudgetMilliseconds);
        Assert.Equal(
            27,
            scheduler.GetUnitAllowance(
                GpuWorkDomain.PunctualShadows,
                6));
    }

    [Fact]
    public void PunctualShadowBudget_IgnoresBriefFramePressure()
    {
        var scheduler = new GpuWorkScheduler();
        for (int sample = 0; sample < 4; ++sample)
            scheduler.RecordFrameGpuTime(9.0);

        for (int sample = 0; sample < 7; ++sample)
            scheduler.RecordFrameGpuTime(40.0);

        GpuWorkBudgetSnapshot punctual = scheduler.GetSnapshots()[1];
        Assert.Equal(30, punctual.MaximumUnits);
        Assert.Equal(6.75, punctual.BudgetMilliseconds);
    }

    [Fact]
    public void PunctualShadowBudget_ShrinksUnderSustainedFramePressure()
    {
        var scheduler = new GpuWorkScheduler();
        for (int sample = 0; sample < 4; ++sample)
            scheduler.RecordFrameGpuTime(9.0);

        for (int sample = 0; sample < 8; ++sample)
            scheduler.RecordFrameGpuTime(14.5);

        GpuWorkBudgetSnapshot punctual = scheduler.GetSnapshots()[1];
        Assert.Equal(24, punctual.MaximumUnits);
        Assert.Equal(6.0, punctual.BudgetMilliseconds);
    }

    [Fact]
    public void PunctualShadowBudget_ClampsAdaptiveRange()
    {
        var scheduler = new GpuWorkScheduler();

        for (int sample = 0; sample < 64; ++sample)
            scheduler.RecordFrameGpuTime(8.0);

        GpuWorkBudgetSnapshot punctual = scheduler.GetSnapshots()[1];
        Assert.Equal(48, punctual.MaximumUnits);
        Assert.Equal(9.0, punctual.BudgetMilliseconds);
    }

    [Fact]
    public void PunctualShadowBatch_HoldsFourPointOrTwentyFourSpotLights()
    {
        Assert.Equal(
            4,
            PunctualShadowPass.GetMaximumLightsPerBatch(6));
        Assert.Equal(
            24,
            PunctualShadowPass.GetMaximumLightsPerBatch(1));
    }

    [Fact]
    public void PunctualShadowBudget_AdmitsFacePairsAtomically()
    {
        var scheduler = new GpuWorkScheduler();
        scheduler.BeginFrame(1);

        Assert.True(
            scheduler.TryAdmit(
                GpuWorkDomain.PunctualShadows,
                2));
        Assert.False(
            scheduler.TryAdmit(
                GpuWorkDomain.PunctualShadows,
                31));

        GpuWorkBudgetSnapshot punctual = scheduler.GetSnapshots()[1];
        Assert.Equal(2, punctual.AdmittedUnits);
        Assert.Equal(31, punctual.DeferredUnits);
        Assert.Equal(2, punctual.TotalAdmittedUnits);
        Assert.Equal(31, punctual.TotalDeferredUnits);
    }

    [Fact]
    public void PunctualShadowBatch_UsesDisjointIndirectCommandRegions()
    {
        Assert.Equal(
            0ul,
            PunctualShadowPass.GetDrawCommandOffset(0, 100));
        Assert.Equal(
            1600ul,
            PunctualShadowPass.GetDrawCommandOffset(1, 100));
        Assert.Equal(
            4800ul,
            PunctualShadowPass.GetDrawCommandOffset(3, 100));
    }

    [Fact]
    public void BeginFrame_ResetsAdmissionCounters()
    {
        var scheduler = new GpuWorkScheduler();
        scheduler.BeginFrame(1);
        Assert.True(scheduler.TryAdmit(GpuWorkDomain.Shadows));

        scheduler.BeginFrame(2);

        Assert.True(scheduler.TryAdmit(GpuWorkDomain.Shadows));
        GpuWorkBudgetSnapshot shadow = scheduler.GetSnapshots()[0];
        Assert.Equal(1, shadow.AdmittedUnits);
        Assert.Equal(0, shadow.DeferredUnits);
    }

    [Fact]
    public void CompletedWork_UpdatesEstimatedUnitCost()
    {
        var scheduler = new GpuWorkScheduler();

        scheduler.RecordCompletedWork(GpuWorkDomain.Shadows, 4.0, 2);

        GpuWorkBudgetSnapshot shadow = scheduler.GetSnapshots()[0];
        Assert.Equal(1.2, shadow.EstimatedUnitMilliseconds, 6);
    }
}
