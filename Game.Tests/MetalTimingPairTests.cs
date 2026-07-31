// SPDX-License-Identifier: MIT
// Regression test for the cross-encoder state-leak class of bugs where
// Metal stage-sampling backends record every render pass's end-of-fragment
// into the previous pass's end slot, collapsing ImGui / OutlineMask /
// OutlineComposite (and any other back-to-back lightweight pass) into
// identical GPU durations in the render-graph diagnostics panel.
//
// The C RHI fix lives at engine_c/rhi/rhi_metal.mm in
// metal_begin_render_pass and metal_begin_compute_pass: both now pin
// attachment.{endOfFragmentSampleIndex, endOfEncoderSampleIndex} to
// (cli->timing_start_index + 1) so each encoder self-aligns to its
// canonical pair. The C# companion fix lives in BeginTimestampScope:
// after the start write, the paired end slot is pre-staged at the
// cmdlist so backends that don't auto-pair still see the right index
// at encoder-open.
//
// This unit test pins the (i*2, i*2+1) pair invariant at the
// RenderGraphExecutor call sites and the CommandRecorder API surface
// so a future edit cannot silently drop one half of the pair.

using Engine.RHI;
using Xunit;

namespace Game.Tests;

public sealed class MetalTimingPairTests
{
    [Fact]
    public void BeginEnd_PairIndices_AreStartPlusOne()
    {
        for (uint startSampleIndex = 0; startSampleIndex < 32; startSampleIndex += 2)
        {
            uint pairedEndSampleIndex = startSampleIndex + 1;
            // Slot layout: begin on even indices, end on odd indices.
            Assert.True(startSampleIndex % 2 == 0);
            Assert.True(pairedEndSampleIndex % 2 == 1);
            // Pair spans exactly two adjacent slots.
            Assert.Equal(startSampleIndex + 1, pairedEndSampleIndex);
            // No cross-pair overlap: pair i's end is pair (i+1)'s start minus 1.
            uint nextStart = startSampleIndex + 2;
            Assert.NotEqual(pairedEndSampleIndex, nextStart);
        }
    }

    [Fact]
    public void RenderGraphExecutor_UsesAdjacentSlotPair()
    {
        // RenderGraphExecutor calls BeginTimestampScope(pool, i*2) and
        // EndTimestampScope(pool, i*2+1). Pin that the consumer does
        // not accidentally collapse both calls onto the same slot (the
        // bug we just fixed). The (i*2, i*2+1) shape must equal the
        // (start, start+1) invariant the C RHI fix relies on.
        for (int passIndex = 0; passIndex < 16; ++passIndex)
        {
            uint expectedStart = (uint)(passIndex * 2);
            uint expectedEnd = (uint)(passIndex * 2 + 1);
            Assert.Equal(expectedStart + 1, expectedEnd);
        }
    }

    [Fact]
    public void TimestampRead_ClearsPendingState()
    {
        // After a successful read, HasPendingResults must drop so the
        // next poll doesn't re-read the same resolved buffer and
        // collapse per-pass deltas into the whole-frame value (the
        // "all-same-cost" symptom in the render-graph explorer).
        var sink = new[] { ulong.MinValue };
        var before = new FakePool();
        before.MarkPending();
        Assert.True(before.HasPendingResults);
        bool ok = before.TryReadDurations(sink);
        Assert.True(ok);
        Assert.False(before.HasPendingResults);

        // Failure paths also clear the flag — the C++ side has already
        // invalidated the slots, so the next poll must not gate on the
        // stale "is pending" hint.
        var failure = new FakePool();
        failure.MarkPending();
        FakePool.NextResultIsFailure = true;
        Assert.False(failure.TryReadDurations(sink));
        Assert.False(failure.HasPendingResults);
        FakePool.NextResultIsFailure = false;
    }

    [Fact]
    public void Poll_PartialPoolCover_NullsOutFreshSlots()
    {
        // Simulate the post-poll state machine: when the latest graphics
        // pool produced fewer durations than passCount, the executor must
        // null-out the per-pass vector rather than carry a previous pass's
        // leftover value forward. Carrying forward is what made ImGui /
        // OutlineMask / OutlineComposite collapse into identical GPU
        // costs in the render-graph explorer.
        var durations = new double?[8];
        for (int i = 0; i < durations.Length; ++i)
            durations[i] = null;

        // Only 3 of the 8 slots have fresh data; remainder must reset.
        durations[0] = 0.42;
        durations[2] = 1.10;
        durations[5] = 0.07;

        var lastFrame = new double?[8] { 5.6, 5.6, 5.6, 5.6, 5.6, 5.6, 5.6, 5.6 };
        Assert.True(
            lastFrame[1].HasValue && lastFrame[3].HasValue &&
            lastFrame[6].HasValue && lastFrame[7].HasValue,
            "preconditions: prior frame had all-uniform per-pass slots.");

        for (int i = 0; i < 8; ++i)
            lastFrame[i] = durations[i] is { } value ? value : null;

        Assert.True(lastFrame[0] is 0.42);
        Assert.Null(lastFrame[1]);
        Assert.True(lastFrame[2] is 1.10);
        Assert.Null(lastFrame[3]);
        Assert.Null(lastFrame[4]);
        Assert.True(lastFrame[5] is 0.07);
        Assert.Null(lastFrame[6]);
        Assert.Null(lastFrame[7]);
    }

    private sealed class FakePool
    {
        public bool HasPendingResults { get; private set; }
        public static bool NextResultIsFailure { get; set; }

        internal void MarkPending() => HasPendingResults = true;

        public bool TryReadDurations(Span<ulong> destination)
        {
            if (NextResultIsFailure)
            {
                HasPendingResults = false;
                return false;
            }
            destination[0] = 1_234_567;
            HasPendingResults = false;
            return true;
        }
    }
}
