// SPDX-License-Identifier: MIT
// In-process generation-tracked cache for compiled RhiShader handles,
// keyed on caller-provided stable strings (typically content-root +
// source path + RendererFeatureSet.FeatureSetHash of active features).
// Generation-counter-based eviction disposes stale entries on plugin
// toggle so the next renderer rebuild compiles against the new feature
// set without leaking old GPU artifacts.
//
// Phase-3 speedup layer: a parallel content-hash keyed dictionary
// (SHA256 over resolved source + entry + stage + includeDirs + cliArgs)
// returns the cached RhiShader handle when the inputs hash-equivalent.
// Plugin toggles that don't actually change a shader's content now
// hit the cache instead of forcing a Slang recompile + Metal pipeline
// state recreation, cutting the editor "shader-toggle-freeze" cost
// for plugins whose overrides only flip `-D NAME=0/1` cliArgs.
//
// Both dictionaries share disposal lifecycle — disposing the cache
// closes every cached handle regardless of which dictionary it lives
// in.
//
// Disposal detection: every cache-hit path consults
// <see cref="RhiShader.IsAlive"/> on the cached wrapper and treats a
// false return as a cache miss. This is the safety net for the case
// where the original holder of the cached RhiShader was disposed
// outside the cache (e.g. by an earlier plan's render-pass lifecycle
// during plugin toggle / scene reload) without the cache observing
// the disposal; without this guard, the cache would return a wrapper
// whose <see cref="RhiShader.Handle"/> is <see cref="IntPtr.Zero"/>
// and the consumer's <see cref="RhiPipeline.CreateCompute"/> would
// throw <see cref="ObjectDisposedException"/> on the next plan build.
// See docs/renderer/shader-cache.md#disposal-detection.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Engine.RHI;
using Engine.CBindings;

namespace Engine.RHI;

public sealed class ShaderCompileCache : IDisposable
{
    // Boundary bytes used by ComputeContentKey to separate the
    // (entry, stage, source) group from (includeDirs) and (includeDirs)
    // from (cliArgs). Length-prefixed UTF-8 inputs already prevent
    // adjacent-string collisions; these markers pin semantic group
    // boundaries so e.g. an extra trailing empty cliArgs entry cannot
    // yield the same hash as removing it.
    private static readonly byte[] s_groupSourceEnd = { 0xFE };
    private static readonly byte[] s_groupIncludeEnd = { 0xFF };

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry> _contentEntries = new(StringComparer.Ordinal);
    private int _currentGeneration;

    private sealed class Entry
    {
        public required IDisposable Value { get; init; }
        public required int Generation { get; init; }
    }

    public int CurrentGeneration
    {
        get { lock (_lock) { return _currentGeneration; } }
    }

    public int EntryCount
    {
        get { lock (_lock) { return _entries.Count + _contentEntries.Count; } }
    }

    public void BumpGeneration()
    {
        lock (_lock) { _currentGeneration++; }
    }

    public IDisposable GetOrCompile(string cacheKey, Func<string, IDisposable> factory)
    {
        ArgumentNullException.ThrowIfNull(cacheKey);
        ArgumentNullException.ThrowIfNull(factory);
        lock (_lock)
        {
            if (_entries.TryGetValue(cacheKey, out Entry? existing))
            {
                // MARK: Disposal detection — see file header. If the
                // original holder's Dispose ran outside the cache
                // (typical for renderer-pass disposal on plan rebuild or
                // plugin toggle), the cached wrapper now has Handle ==
                // IntPtr.Zero. Treat it as a cache miss so the factory
                // runs a fresh compile and re-populates the entry with
                // a live wrapper. Do NOT call Dispose() on the dead
                // wrapper — RhiShader.Dispose is idempotent on the
                // IntPtr.Zero short-circuit and its pinned handles are
                // already freed.
                if (IsUsableForReturn(existing.Value))
                {
                    _entries[cacheKey] = new Entry
                    {
                        Value = existing.Value,
                        Generation = _currentGeneration,
                    };
                    return existing.Value;
                }
                _entries.Remove(cacheKey);
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

    /// <summary>
    /// Content-equivalent cache lookup keyed by SHA256(source + entry +
    /// stage + includeDirs + cliArgs). A hit returns the cached
    /// RhiShader instance; a miss invokes <paramref name="factory"/>.
    /// Unlike <see cref="GetOrCompile"/>, callers do not supply the key
    /// directly — they supply the inputs that <see cref="ComputeContentKey"/>
    /// folds into a stable SHA256 digest.
    /// </summary>
    /// <remarks>
    /// The cached handle's generation tag is refreshed on hit so
    /// <see cref="EvictOlderThan"/> keeps it across plugin toggles.
    /// Phase-3's speedup comes from the fact that most plugin-toggle
    /// rebuilds reissue the same shaders with the same source bytes;
    /// the cache returns the existing handle, both Slang and Metal
    /// pipeline-state-creating paths are skipped.
    /// </remarks>
    public IDisposable GetOrCompileHash(
        string source, string entry, RhiNative.ShaderStage stage,
        IReadOnlyList<string>? includeDirs, IReadOnlyList<string>? cliArgs,
        Func<IDisposable> factory)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(factory);
        string contentKey = ComputeContentKey(source, entry, stage, includeDirs, cliArgs);
        lock (_lock)
        {
            if (_contentEntries.TryGetValue(contentKey, out Entry? existing))
            {
                // MARK: Disposal detection — see file header. The
                // content-hash dictionary returns the SAME RhiShader
                // instance across phase-3 plugin-toggle rebuilds; if a
                // previous holder's Dispose ran outside the cache, the
                // wrapper now has Handle == IntPtr.Zero. Treat the hit
                // as a miss so the factory compiles fresh.
                if (IsUsableForReturn(existing.Value))
                {
                    _contentEntries[contentKey] = new Entry
                    {
                        Value = existing.Value,
                        Generation = _currentGeneration,
                    };
                    return existing.Value;
                }
                _contentEntries.Remove(contentKey);
            }
            IDisposable compiled = factory();
            _contentEntries[contentKey] = new Entry
            {
                Value = compiled,
                Generation = _currentGeneration,
            };
            return compiled;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the cached <see cref="IDisposable"/>
    /// wrapper is safe to hand back to a consumer. Currently accepts
    /// <c>null</c> (defensive — should not occur since we never store
    /// null) and <see cref="RhiShader"/> instances whose
    /// <see cref="RhiShader.IsAlive"/> flag is still true. Anything
    /// else (a disposed RhiShader, or a future IDisposable type whose
    /// wrapper is not a shader) is treated as a miss so the caller
    /// runs the factory.
    /// </summary>
    private static bool IsUsableForReturn(IDisposable value)
    {
        if (value == null) return false;
        if (value is RhiShader shader) return shader.IsAlive;
        // Non-shader cache users (none today; reserved for future
        // pipeline / texture caches if they co-locate) skip the
        // disposal check — they own their own wrapper invariants.
        return true;
    }

    /// <summary>
    /// Stable SHA256 hash over (entry + stage + source + includeDirs +
    /// cliArgs). Phase-3 callers feed the same inputs through this
    /// function and look up <see cref="GetOrCompileHash"/>; identical
    /// inputs produce identical keys so Slang recompile and Metal
    /// pipeline-state allocation collapse to a dictionary hit.
    /// Implementation uses <see cref="IncrementalHash"/> so the
    /// per-call allocation footprint is a single hash handle + one
    /// UTF-8 byte array per input segment, not a fresh
    /// <see cref="SHA256"/> + <see cref="MemoryStream"/> per call.
    /// </summary>
    public static string ComputeContentKey(
        string source, string entry, RhiNative.ShaderStage stage,
        IReadOnlyList<string>? includeDirs, IReadOnlyList<string>? cliArgs)
    {
        using var ih = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] entryBytes = Encoding.UTF8.GetBytes(entry ?? string.Empty);
        if (entryBytes.Length > 0) ih.AppendData(entryBytes);
        Span<byte> stageBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(stageBytes, (uint)(int)stage);
        ih.AppendData(stageBytes);
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source ?? string.Empty);
        if (sourceBytes.Length > 0) ih.AppendData(sourceBytes);
        ih.AppendData(s_groupSourceEnd);
        if (includeDirs != null)
            foreach (string dir in includeDirs)
            {
                byte[] dirBytes = Encoding.UTF8.GetBytes(dir ?? string.Empty);
                if (dirBytes.Length > 0) ih.AppendData(dirBytes);
            }
        ih.AppendData(s_groupIncludeEnd);
        if (cliArgs != null)
            foreach (string arg in cliArgs)
            {
                byte[] argBytes = Encoding.UTF8.GetBytes(arg ?? string.Empty);
                if (argBytes.Length > 0) ih.AppendData(argBytes);
            }
        return Convert.ToHexString(ih.GetHashAndReset());
    }

    public void EvictOlderThan(int maxAge)
    {
        if (maxAge < 0) maxAge = 0;
        lock (_lock)
        {
            int cutoff = _currentGeneration - maxAge;
            EvictStaleFrom(_entries, cutoff);
            EvictStaleFrom(_contentEntries, cutoff);
        }
    }

    private static void EvictStaleFrom(Dictionary<string, Entry> dict, int cutoff)
    {
        List<string>? staleKeys = null;
        foreach (var kv in dict)
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
            try { dict[key].Value.Dispose(); }
            catch { /* best effort */ }
            dict.Remove(key);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            DisposeEntries(_entries);
            DisposeEntries(_contentEntries);
        }
    }

    private static void DisposeEntries(Dictionary<string, Entry> dict)
    {
        foreach (var entry in dict.Values)
        {
            try { entry.Value.Dispose(); }
            catch { /* best effort */ }
        }
        dict.Clear();
    }
}
