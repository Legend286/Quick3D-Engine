// SPDX-License-Identifier: MIT

using Engine.CBindings;
using Engine.RenderGraph;
using Xunit;

namespace Engine.Game.Tests;

public sealed class RenderGraphQueueTests
{
    [Fact]
    public void ComputeOutput_CreatesGraphicsConsumerBarrier()
    {
        ResourceHandle resource = new(0x7000FFF0);
        RenderPass compute = new TestPass(
            "Compute",
            RhiNative.QueueType.Compute,
            builder => builder.Write(resource, ResourceState.UnorderedAccess));
        RenderPass graphics = new TestPass(
            "Graphics",
            RhiNative.QueueType.Graphics,
            builder => builder.Read(resource, ResourceState.ShaderRead));

        RenderPlan plan = new RenderGraphCompiler().Compile(
            new[] { compute, graphics });

        Assert.Equal(RhiNative.QueueType.Compute, plan.Passes[0].Queue);
        Assert.Contains(
            plan.BarriersPerPass[1],
            barrier =>
                barrier.Resource == resource &&
                barrier.StateBefore == ResourceState.UnorderedAccess &&
                barrier.StateAfter == ResourceState.ShaderRead);
    }

    private sealed class TestPass : RenderPass
    {
        private readonly Action<RenderGraphBuilder> _setup;

        public TestPass(
            string name,
            RhiNative.QueueType queue,
            Action<RenderGraphBuilder> setup)
        {
            Name = name;
            Queue = queue;
            _setup = setup;
        }

        public override void Setup(RenderGraphBuilder builder) => _setup(builder);

        public override void Execute(
            ICommandSink sink,
            RenderGraphContext context)
        {
        }
    }
}
