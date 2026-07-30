// SPDX-License-Identifier: MIT
// In-process generation-tracked cache for compiled RhiShader handles,
// keyed on caller-provided stable strings (typically content-root +
// source path + RendererFeatureSet.FeatureSetHash of active features).
// Generation-counter-based eviction disposes stale entries on plugin
// toggle so the next renderer rebuild compiles against the new feature
// set without leaking old GPU artifacts.

using System;
using System.Collections.Generic;

namespace Engine.Renderer;

/// <summary>
/// Generation-tracked cache of disposable shader handles.
/// </summary>
/// <remarks>
/// Stores <see cref="IDisposable"/> rather than the concrete
/// <see cref="Engine.RHI.RhiShader"/> type so this class remains
/// unit-testable without a native ABI. The renderer holds an instance
/// and supplies a factory closure that hands out RhiShader handles via
/// the GetOrCompile callback; the cache itself only owns the
/// <see cref="IDisposable"/> lifecycle.
/// </remarks>
public sealed class ShaderCompileCache : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private int _currentGeneration;

    private sealed class Entry
    {
        public required IDisposable Value { get; init; }
        public required int Generation { get; init; }
    }

    /// <summary>Current generation counter (monotonically increasing).</summary>
    public int CurrentGeneration
    {
        get { lock (_lock) { return _currentGeneration; } }
    }

    /// <summary>Number of cached entries currently held.</summary>
    public int EntryCount
    {
        get { lock (_lock) { return _entries.Count; } }
    }

    /// <summary>Increments <see cref="CurrentGeneration"/>; caller should
    /// follow up with <see cref="EvictOlderThan"/> to dispose stale
    /// entries.</summary>
    public void BumpGeneration()
    {
        lock (_lock) { _currentGeneration++; }
    }

    /// <summary>Returns the cached entry for <paramref name="cacheKey"/>
    /// (refreshing its generation tag to "alive") or calls
    /// <paramref name="factory"/> to produce one if absent. The factory
    /// is invoked at most once per cache key.</summary>
    public IDisposable GetOrCompile(string cacheKey, Func<string, IDisposable> factory)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        ArgumentNullException.ThrowIfNull(factory);
        lock (_lock)
        {
            if (_entries.TryGetValue(cacheKey, out Entry? existing))
            {
                _entries[cacheKey] = new Entry
                {
                    Value = existing.Value,
                    Generation = _currentGeneration,
                };
                return existing.Value;
            }
            IDisposable compiled = factory(cacheKey);
            _entries[cacheKey] = new Entry
            {
                Value = compiled,
                Generation = _currentGeneration,
            };
            return compiled;
        }
    }

    /// <summary>Disposes every entry whose generation lags current by
    /// more than <paramref name="maxAge"/> generations.</summary>
    public void EvictOlderThan(int maxAge)
    {
        if (maxAge < 0) maxAge = 0;
        lock (_lock)
        {
            int cutoff = _currentGeneration - maxAge;
            List<string>? staleKeys = null;
            foreach (var kv in _entries)
            {
                if (kv.Value.Generation < cutoff)
                {
                    staleKeys ??= new List<string>();
                    staleKeys.Add(kv.Key);
                }
            }
            if (staleKeys == null) return;
            foreach (string key in staleKeys)
            {
                try { _entries[key].Value.Dispose(); }
                catch { /* best effort */ }
                _entries.Remove(key);
            }
        }
    }

    /// <summary>Disposes every cached entry. The cache is unusable
    /// after disposal.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var entry in _entries.Values)
            {
                try { entry.Value.Dispose(); }
                catch { /* best effort */ }
            }
            _entries.Clear();
        }
    }
}
