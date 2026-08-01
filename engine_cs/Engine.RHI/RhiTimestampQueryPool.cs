// SPDX-License-Identifier: MIT

using System;
using Engine.CBindings;

namespace Engine.RHI;

/// <summary>
/// Owns a backend timestamp-query pool and exposes non-blocking duration reads.
/// </summary>
public sealed class RhiTimestampQueryPool : IDisposable
{
    public IntPtr Handle { get; private set; }
    public uint SampleCount { get; }
    public bool HasPendingResults { get; private set; }

    private RhiTimestampQueryPool(IntPtr handle, uint sampleCount)
    {
        Handle = handle;
        SampleCount = sampleCount;
    }

    /// <summary>
    /// Attempts to create a timestamp pool, returning null when unsupported.
    /// </summary>
    public static RhiTimestampQueryPool? TryCreate(
        RhiDevice device,
        uint sampleCount,
        uint samplesPerDuration = 2)
    {
        if (sampleCount == 0)
            return null;

        int result = RhiNative.RhiCreateTimestampQueryPool(
            device.Handle,
            sampleCount,
            out IntPtr handle);
        if (result != 0 || handle == IntPtr.Zero)
            return null;
        if (RhiNative.RhiTimestampQueryPoolSetSamplesPerDuration(
                handle,
                samplesPerDuration) != 0)
        {
            RhiNative.RhiDestroyTimestampQueryPool(handle);
            return null;
        }
        return new RhiTimestampQueryPool(handle, sampleCount);
    }

    /// <summary>
    /// Reads logical pass durations without waiting for completion.
    /// Once the C++ side resolves the command buffer's timestamps into
    /// <c>pi->results</c>, only one successful read is meaningful — the
    /// native side reduces each pass's encoder pairs and clears
    /// <c>pi->pending</c> after consumption, so a
    /// subsequent poll with <see cref="HasPendingResults"/> still true
    /// would otherwise silently re-read the same resolved buffer and
    /// collapse the per-pass deltas into the whole-frame value.
    /// Clearing <see cref="HasPendingResults"/> on every terminal result
    /// (success and error alike) keeps the per-pass durations isolated
    /// across polls and prevents the upstream "all-same-cost" symptom
    /// in the render-graph explorer.
    /// </summary>
    public unsafe bool TryReadDurations(Span<ulong> durationNanoseconds)
    {
        if (Handle == IntPtr.Zero || durationNanoseconds.IsEmpty)
        {
            HasPendingResults = false;
            return false;
        }

        fixed (ulong* durations = durationNanoseconds)
        {
            int result = RhiNative.RhiTimestampQueryPoolReadDurations(
                Handle,
                (uint)durationNanoseconds.Length,
                (IntPtr)durations);
            if (result != 0)
            {
                HasPendingResults = false;
            }
            return result == 1;
        }
    }

    /// <summary>
    /// Reads the completed command buffer's total GPU duration without waiting.
    /// </summary>
    public bool TryReadFrameDuration(out ulong durationNanoseconds)
    {
        durationNanoseconds = 0;
        if (Handle == IntPtr.Zero || !HasPendingResults)
            return false;
        return RhiNative.RhiTimestampQueryPoolReadFrameDuration(
            Handle,
            out durationNanoseconds) == 1;
    }

    internal void MarkPending()
    {
        HasPendingResults = true;
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero)
            return;

        RhiNative.RhiDestroyTimestampQueryPool(Handle);
        Handle = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }
}
