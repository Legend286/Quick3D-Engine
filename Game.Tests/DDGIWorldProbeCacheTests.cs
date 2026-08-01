// SPDX-License-Identifier: MIT
using System.Numerics;
using Engine.DDGI;
using Xunit;

namespace Engine.Game.Tests;

public sealed class DDGIWorldProbeCacheTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(4, 2)]
    [InlineData(16, 8)]
    [InlineData(32, 16)]
    [InlineData(128, 64)]
    public void SceneBakeBudget_StaysWithinUpdateCapacity(
        int updateAllowance,
        int expectedBakeRequests)
    {
        Assert.Equal(
            expectedBakeRequests,
            DDGIProbePlacementPass.CalculateSceneBakeRequestBudget(
                updateAllowance));
    }

    [Fact]
    public void CameraReturn_ReusesPersistentPhysicalSlots()
    {
        var cache = new DDGIWorldProbeCache(
            4096,
            3,
            2,
            new Vector3(2.0f),
            4.0f);

        cache.PrepareFrame(
            Vector3.Zero,
            1u,
            false,
            Vector3.Zero,
            Vector3.Zero);
        DDGIProbeRequest initial = FindGridRequest(cache, 0u);

        cache.PrepareFrame(
            new Vector3(128.0f, 0.0f, 0.0f),
            1u,
            false,
            Vector3.Zero,
            Vector3.Zero);
        cache.PrepareFrame(
            Vector3.Zero,
            1u,
            false,
            Vector3.Zero,
            Vector3.Zero);
        DDGIProbeRequest returned = FindGridRequest(cache, 0u);

        Assert.Equal(initial.ProbeSlot, returned.ProbeSlot);
        Assert.Equal(
            0u,
            returned.Flags & DDGIWorldProbeCache.NewAllocationFlag);
    }

    [Fact]
    public void SceneBake_StartsAtFarCascadeAndStaysBounded()
    {
        var cache = new DDGIWorldProbeCache(
            8192,
            3,
            3,
            new Vector3(2.0f),
            4.0f);

        cache.PrepareFrame(
            Vector3.Zero,
            7u,
            true,
            new Vector3(512.0f, 0.0f, 512.0f),
            new Vector3(640.0f, 64.0f, 640.0f),
            DDGIWorldProbeCache.MaxSceneBakeRequestsPerFrame);

        int bakeRequests = 0;
        foreach (DDGIProbeRequest request in cache.Requests)
        {
            if ((request.Flags & DDGIWorldProbeCache.SceneBakeFlag) == 0u)
                continue;
            ++bakeRequests;
            Assert.Equal(2u, request.ClipmapLevel);
        }

        Assert.Equal(
            DDGIWorldProbeCache.MaxSceneBakeRequestsPerFrame,
            bakeRequests);
        Assert.True(cache.BakeActive);
    }

    [Fact]
    public void SceneBake_PausesUntilGeometryClassificationIsAvailable()
    {
        var cache = new DDGIWorldProbeCache(
            8192,
            3,
            3,
            new Vector3(2.0f),
            4.0f);

        cache.PrepareFrame(
            Vector3.Zero,
            7u,
            true,
            new Vector3(512.0f, 0.0f, 512.0f),
            new Vector3(640.0f, 64.0f, 640.0f),
            DDGIWorldProbeCache.DefaultSceneBakeRequestsPerFrame,
            canClassifySceneBake: false);

        Assert.True(cache.BakeActive);
        Assert.DoesNotContain(
            cache.Requests.ToArray(),
            request =>
                (request.Flags & DDGIWorldProbeCache.SceneBakeFlag) != 0u);

        cache.PrepareFrame(
            Vector3.Zero,
            7u,
            true,
            new Vector3(512.0f, 0.0f, 512.0f),
            new Vector3(640.0f, 64.0f, 640.0f),
            DDGIWorldProbeCache.DefaultSceneBakeRequestsPerFrame,
            canClassifySceneBake: true);

        Assert.Equal(
            DDGIWorldProbeCache.DefaultSceneBakeRequestsPerFrame,
            cache.Requests.ToArray().Count(request =>
                (request.Flags & DDGIWorldProbeCache.SceneBakeFlag) != 0u));
    }

    [Fact]
    public void FullCache_PreservesExistingSlotsWithoutEviction()
    {
        const int gridResolution = 3;
        const int clipmapLevels = 1;
        const int capacity =
            gridResolution * gridResolution * gridResolution;
        var cache = new DDGIWorldProbeCache(
            capacity,
            gridResolution,
            clipmapLevels,
            new Vector3(2.0f),
            4.0f);

        cache.PrepareFrame(
            Vector3.Zero,
            1u,
            false,
            Vector3.Zero,
            Vector3.Zero);
        DDGIProbeRequest initial = FindGridRequest(cache, 0u);
        Assert.Equal(capacity, cache.AllocatedProbeCount);

        cache.PrepareFrame(
            new Vector3(128.0f, 0.0f, 0.0f),
            1u,
            false,
            Vector3.Zero,
            Vector3.Zero);
        Assert.Equal(capacity, cache.AllocatedProbeCount);

        cache.PrepareFrame(
            Vector3.Zero,
            1u,
            false,
            Vector3.Zero,
            Vector3.Zero);
        DDGIProbeRequest returned = FindGridRequest(cache, 0u);
        Assert.Equal(initial.ProbeSlot, returned.ProbeSlot);
    }

    [Fact]
    public void OverlappingClipmaps_KeepEveryLevelAvailable()
    {
        var cache = new DDGIWorldProbeCache(
            8192,
            3,
            3,
            new Vector3(2.0f),
            4.0f);

        cache.PrepareFrame(
            Vector3.Zero,
            1u,
            false,
            Vector3.Zero,
            Vector3.Zero);

        Assert.Equal(81, cache.Requests.Length);
        Assert.All(
            cache.Requests.ToArray(),
            request => Assert.Equal(0u, request.Flags & 4u));
    }

    private static DDGIProbeRequest FindGridRequest(
        DDGIWorldProbeCache cache,
        uint gridCellIndex)
    {
        foreach (DDGIProbeRequest request in cache.Requests)
        {
            if (request.GridCellIndex == gridCellIndex)
                return request;
        }
        throw new InvalidOperationException(
            $"Grid request {gridCellIndex} was not generated.");
    }
}
