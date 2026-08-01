// SPDX-License-Identifier: MIT

using System.IO;
using System.Linq;
using System.Reflection;
using Engine.RHI;
using Engine.RenderGraph;
using Xunit;

namespace Game.Tests;

public sealed class MetalTimingPairTests
{
    [Fact]
    public void LogicalPassSampleBlocks_DoNotOverlap()
    {
        const uint samplesPerPass = 64;
        for (uint passIndex = 0; passIndex < 16; ++passIndex)
        {
            uint startSampleIndex = passIndex * samplesPerPass;
            uint endSampleIndex =
                (passIndex + 1) * samplesPerPass - 1;
            uint nextStart = (passIndex + 1) * samplesPerPass;
            Assert.Equal(samplesPerPass - 1, endSampleIndex - startSampleIndex);
            Assert.Equal(endSampleIndex + 1, nextStart);
        }
    }

    [Fact]
    public void RenderGraphExecutor_UsesPerPassSampleBlocks()
    {
        const uint samplesPerPass = 64;
        for (int passIndex = 0; passIndex < 16; ++passIndex)
        {
            uint expectedStart = (uint)passIndex * samplesPerPass;
            uint expectedEnd =
                (uint)(passIndex + 1) * samplesPerPass - 1;
            Assert.True(expectedEnd > expectedStart);
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

    [Fact]
    public void PublishPassTimings_ClearsUnsampledPassSlots()
    {
        MethodInfo publish = typeof(RenderGraphExecutor).GetMethod(
            "PublishPassTimings",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var graphics = new double?[] { 0.42, null, 1.10, null };
        var compute = new double?[] { null, 0.25, null, null };
        var destination = new double?[] { 9.0, 9.0, 9.0, 9.0 };

        publish.Invoke(
            null,
            new object?[] { graphics, compute, destination });

        Assert.Equal(0.42, destination[0]);
        Assert.Equal(0.25, destination[1]);
        Assert.Equal(1.10, destination[2]);
        Assert.Null(destination[3]);
    }

    [Fact]
    public void MetalBackend_PrefersExplicitPassBoundarySamples()
    {
        string source = ReadRepositoryFile(
            "engine_c", "rhi", "rhi_metal.mm");

        Assert.Contains(
            "supports_stage_sampling &&\n                !cli->timing_pool->supports_draw_sampling",
            source);
        Assert.Contains(
            "supports_stage_sampling &&\n                !cli->timing_pool->supports_dispatch_sampling",
            source);
        Assert.Contains(
            "ri->render && cli->timing_pool->supports_draw_sampling",
            source);
        Assert.Contains(
            "ri->compute &&\n                           cli->timing_pool->supports_dispatch_sampling",
            source);
    }

    [Fact]
    public void RenderGraphDiagnostics_PublishesSampledGpuWork()
    {
        string executor = ReadRepositoryFile(
            "engine_cs", "Engine.RenderGraph", "RenderGraphExecutor.cs");
        string renderer = ReadRepositoryFile(
            "engine_cs", "Engine.Renderer", "Renderer.cs");

        Assert.Contains("_cpuTimingCaptures", executor);
        Assert.Contains("CpuTimingHistoryCapacity = 32", executor);
        Assert.Contains("PublishCoherentPassTimings", executor);
        Assert.Contains("TimestampSamplesPerPass = 64", executor);
        Assert.Contains("SumPassTimings", executor);
        Assert.Contains("LastGpuWorkMilliseconds", executor);
        Assert.DoesNotContain("sampledTotalMilliseconds", executor);
        Assert.Contains("LastGpuTimingFrameNumber", renderer);
        Assert.Contains("LastRawGpuFrameMilliseconds", renderer);
        Assert.Contains("LastGpuWorkMilliseconds", renderer);
    }

    [Fact]
    public void MetalBackend_AssignsUniqueEncoderPairsWithinPassBlock()
    {
        string source = ReadRepositoryFile(
            "engine_c", "rhi", "rhi_metal.mm");
        string header = ReadRepositoryFile(
            "engine_c", "rhi", "rhi.h");
        string bindings = ReadRepositoryFile(
            "OutOfBand", "Engine.CBindings", "Rhi.cs");

        Assert.Contains("samples_per_duration", source);
        Assert.Contains("metal_allocate_timing_samples", source);
        Assert.Contains("metal_mark_timing_pair", source);
        Assert.Contains("sample_roles", source);
        Assert.Contains("stage_timing_first + 3", source);
        Assert.Contains(
            "rhi_timestamp_query_pool_set_samples_per_duration",
            header);
        Assert.Contains(
            "RhiTimestampQueryPoolSetSamplesPerDuration",
            bindings);
    }

    [Fact]
    public void SumPassTimings_IgnoresUnsampledAndInvalidValues()
    {
        MethodInfo sum = typeof(RenderGraphExecutor).GetMethod(
            "SumPassTimings",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var timings = new double?[] { 0.25, null, 1.5, double.NaN, -2.0 };

        double result = (double)sum.Invoke(null, new object[] { timings })!;

        Assert.Equal(1.75, result);
    }

    private static string ReadRepositoryFile(params string[] parts)
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        for (int depth = 0; depth < 10; ++depth)
        {
            string candidate = Path.Combine(
                new[] { directory }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            DirectoryInfo? parent = Directory.GetParent(directory);
            if (parent == null)
                break;
            directory = parent.FullName;
        }
        throw new FileNotFoundException(string.Join('/', parts));
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
