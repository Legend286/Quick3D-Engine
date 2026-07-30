// SPDX-License-Identifier: MIT
// Tests ShaderCompileCache.GetOrCompile + EvictOlderThan + Dispose
// lifecycle. Uses a TestDisposable proxy so the cache surface is
// exercised without requiring a native ABI library to be loaded.

using System;
using Engine.Renderer;
using Xunit;

namespace Engine.Game.Tests;

public sealed class ShaderCompileCacheTests : IDisposable
{
    private readonly ShaderCompileCache _cache = new();

    public void Dispose()
    {
        _cache.Dispose();
    }

    [Fact]
    public void CurrentGeneration_StartsAtZero()
    {
        Assert.Equal(0, _cache.CurrentGeneration);
    }

    [Fact]
    public void BumpGeneration_IncrementsCounter()
    {
        _cache.BumpGeneration();
        Assert.Equal(1, _cache.CurrentGeneration);
        _cache.BumpGeneration();
        Assert.Equal(2, _cache.CurrentGeneration);
    }

    [Fact]
    public void GetOrCompile_NullKey_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _cache.GetOrCompile(null!, _ => new TestDisposable()));
    }

    [Fact]
    public void GetOrCompile_NullFactory_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _cache.GetOrCompile("key", null!));
    }

    [Fact]
    public void GetOrCompile_FirstLookup_InvokesFactoryAndCaches()
    {
        int factoryCalls = 0;
        IDisposable result = _cache.GetOrCompile("key", _ =>
        {
            factoryCalls++;
            return new TestDisposable();
        });
        Assert.Equal(1, factoryCalls);
        Assert.NotNull(result);
        Assert.Equal(1, _cache.EntryCount);
    }

    [Fact]
    public void GetOrCompile_RepeatLookup_DoesNotInvokeFactoryAndKeepsIdentity()
    {
        IDisposable first = _cache.GetOrCompile("key", _ => new TestDisposable());
        IDisposable second = _cache.GetOrCompile(
            "key",
            _ => throw new InvalidOperationException(
                "factory should not run on cache hit"));
        Assert.Same(first, second);
        Assert.Equal(1, _cache.EntryCount);
    }

    [Fact]
    public void GetOrCompile_RepeatedAccessRefreshesGenerationTag()
    {
        TestDisposable d = new();
        _cache.GetOrCompile("key", _ => d);
        _cache.BumpGeneration();
        _cache.GetOrCompile("key", _ => d);
        _cache.EvictOlderThan(0);

        Assert.False(d.IsDisposed);
        Assert.Equal(1, _cache.EntryCount);
    }

    [Fact]
    public void EvictOlderThan_DropsGenerationallyStaleEntries()
    {
        TestDisposable d = new();
        _cache.GetOrCompile("key", _ => d);
        _cache.BumpGeneration();
        _cache.EvictOlderThan(0);

        Assert.True(d.IsDisposed);
        Assert.Equal(0, _cache.EntryCount);
    }

    [Fact]
    public void EvictOlderThan_NegativeArgumentTreatedAsZero()
    {
        TestDisposable d = new();
        _cache.GetOrCompile("key", _ => d);
        _cache.BumpGeneration();
        _cache.EvictOlderThan(-3);

        Assert.True(d.IsDisposed);
    }

    [Fact]
    public void EvictOlderThan_PreservesFreshEntries()
    {
        TestDisposable d = new();
        _cache.GetOrCompile("key", _ => d);
        _cache.EvictOlderThan(0);

        Assert.False(d.IsDisposed);
        Assert.Equal(1, _cache.EntryCount);
    }

    [Fact]
    public void Dispose_ReleasesEveryTrackedEntry()
    {
        TestDisposable d1 = new();
        TestDisposable d2 = new();
        _cache.GetOrCompile("key1", _ => d1);
        _cache.GetOrCompile("key2", _ => d2);

        _cache.Dispose();

        Assert.True(d1.IsDisposed);
        Assert.True(d2.IsDisposed);
        Assert.Equal(0, _cache.EntryCount);
    }

    [Fact]
    public void Dispose_TolerantOfPreDisposedFactoryOutput()
    {
        TestDisposable d = new();
        _cache.GetOrCompile("key", _ => d);
        d.Dispose();

        _cache.Dispose();
        Assert.True(d.IsDisposed);
    }

    private sealed class TestDisposable : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }
}
