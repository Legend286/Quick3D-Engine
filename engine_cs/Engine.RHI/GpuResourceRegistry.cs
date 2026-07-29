// SPDX-License-Identifier: MIT

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Engine.RHI;

/// <summary>
/// Tracks live committed GPU allocations for renderer diagnostics.
/// </summary>
public static class GpuResourceRegistry
{
    private static readonly ConcurrentDictionary<long, Entry> Entries = new();
    private static long _nextId;

    private sealed record Entry(
        long Id,
        string Name,
        string Kind,
        string Category,
        ulong SizeBytes);

    internal static long Register(
        string name,
        string kind,
        string category,
        ulong sizeBytes)
    {
        long id = Interlocked.Increment(ref _nextId);
        Entries[id] = new Entry(id, name, kind, category, sizeBytes);
        return id;
    }

    internal static void Rename(
        long id,
        string name,
        string category)
    {
        if (id == 0 || !Entries.TryGetValue(id, out Entry? entry))
            return;
        Entries[id] = entry with
        {
            Name = name,
            Category = category,
        };
    }

    internal static void Unregister(long id)
    {
        if (id != 0)
            Entries.TryRemove(id, out _);
    }

    /// <summary>
    /// Captures an immutable, size-descending snapshot of live allocations.
    /// </summary>
    public static GpuResourceAllocationDiagnostics[] Capture()
        => Entries.Values
            .OrderByDescending(entry => entry.SizeBytes)
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .Select(entry => new GpuResourceAllocationDiagnostics(
                entry.Id,
                entry.Name,
                entry.Kind,
                entry.Category,
                entry.SizeBytes))
            .ToArray();
}
