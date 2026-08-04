// SPDX-License-Identifier: MIT

using Engine.CBindings;
using Engine.RenderGraph;
using Xunit;

namespace Engine.Game.Tests;

public sealed class RenderGraphQueueTests
{
    [Fact]
    public void ReadOnlyStateTransition_UsesPreviousReadState()
    {
        ResourceHandle resource = new(0x7000FFF1);
        RenderPass first = new TestPass(
            "First Read",
            RhiNative.QueueType.Graphics,
            builder => builder.Read(resource, ResourceState.ShaderRead));
        RenderPass second = new TestPass(
            "Second Read",
            RhiNative.QueueType.Graphics,
            builder => builder.Read(resource, ResourceState.IndirectRead));

        RenderPlan plan = new RenderGraphCompiler().Compile(
            new[] { first, second });

        Assert.Contains(
            plan.BarriersPerPass[1],
            barrier =>
                barrier.Resource == resource &&
                barrier.StateBefore == ResourceState.ShaderRead &&
                barrier.StateAfter == ResourceState.IndirectRead);
    }

    [Fact]
    public void OrderedUavWriters_CreateWriteAfterWriteBarrier()
    {
        ResourceHandle resource = new(0x7000FFEE);
        RenderPass first = new TestPass(
            "First UAV Writer",
            RhiNative.QueueType.Graphics,
            builder => builder.Write(resource, ResourceState.UnorderedAccess));
        RenderPass second = new TestPass(
            "Second UAV Writer",
            RhiNative.QueueType.Graphics,
            builder => builder.Write(resource, ResourceState.UnorderedAccess));

        RenderPlan plan = new RenderGraphCompiler().Compile(
            new[] { first, second });

        Assert.Contains(
            plan.BarriersPerPass[1],
            barrier =>
                barrier.Resource == resource &&
                barrier.StateBefore == ResourceState.UnorderedAccess &&
                barrier.StateAfter == ResourceState.UnorderedAccess);
    }

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
