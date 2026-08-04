// SPDX-License-Identifier: MIT

using Engine.CBindings;
using Engine.RenderGraph;
using Xunit;

namespace Engine.Game.Tests;

public sealed class RenderGraphExternalResourceTests
{
    private static readonly ResourceHandle ProbePositions = new(0x61000001);
    private static readonly ResourceHandle Irradiance = new(0x61000002);

    [Fact]
    public void ImportedBuffer_ProducerToConsumer_EmitsBarrierAndSkipsAliasing()
    {
        RenderPass placement = new TestPass(
            "Placement",
            builder =>
            {
                builder.ImportBuffer(ProbePositions);
                builder.Write(
                    ProbePositions,
                    ResourceState.UnorderedAccess);
            });
        RenderPass update = new TestPass(
            "Update",
            builder =>
            {
                builder.ImportBuffer(ProbePositions);
                builder.Read(
                    ProbePositions,
                    ResourceState.ShaderRead);
            });

        RenderPlan plan = new RenderGraphCompiler().Compile(
            new[] { placement, update });

        Assert.True(plan.ResourceDecls[ProbePositions].External);
        Assert.Empty(plan.Aliasing.ResourceOffsets);
        Assert.Equal(0ul, plan.Aliasing.TotalHeapSize);
        Assert.Contains(
            plan.BarriersPerPass[1],
            barrier =>
                barrier.Resource == ProbePositions &&
                barrier.StateBefore == ResourceState.UnorderedAccess &&
                barrier.StateAfter == ResourceState.ShaderRead);
    }

    [Fact]
    public void ImportedIndirectBuffer_ComputeWriteToDrawRead_EmitsIndirectBarrier()
    {
        RenderPass cull = new TestPass(
            "Cull",
            builder =>
            {
                builder.ImportBuffer(ProbePositions);
                builder.Write(
                    ProbePositions,
                    ResourceState.UnorderedAccess);
            });
        RenderPass draw = new TestPass(
            "Draw",
            builder =>
            {
                builder.ImportBuffer(ProbePositions);
                builder.Read(ProbePositions, ResourceState.IndirectRead);
            });

        RenderPlan plan = new RenderGraphCompiler().Compile(
            new[] { cull, draw });

        Assert.Contains(
            plan.BarriersPerPass[1],
            barrier =>
                barrier.Resource == ProbePositions &&
                barrier.StateBefore == ResourceState.UnorderedAccess &&
                barrier.StateAfter == ResourceState.IndirectRead);
    }

    [Fact]
    public void ImportedTexture_UpdateToPbr_EmitsShaderReadBarrier()
    {
        RenderPass update = new TestPass(
            "Update",
            builder =>
            {
                builder.ImportTexture(Irradiance);
                builder.Write(
                    Irradiance,
                    ResourceState.UnorderedAccess);
            });
        RenderPass pbr = new TestPass(
            "PBR",
            builder =>
            {
                builder.ImportTexture(Irradiance);
                builder.Read(Irradiance, ResourceState.ShaderRead);
            });

        RenderPlan plan = new RenderGraphCompiler().Compile(
            new[] { update, pbr });

        Assert.Contains(
            plan.BarriersPerPass[1],
            barrier =>
                barrier.Resource == Irradiance &&
                barrier.StateBefore == ResourceState.UnorderedAccess &&
                barrier.StateAfter == ResourceState.ShaderRead);
    }

    [Fact]
    public void InvalidImportedResourceHandle_IsRejected()
    {
        var builder = new RenderGraphBuilder();

        Assert.Throws<ArgumentException>(
            () => builder.ImportBuffer(ResourceHandle.Invalid));
        Assert.Throws<ArgumentException>(
            () => builder.ImportTexture(ResourceHandle.Invalid));
    }

    [Theory]
    [InlineData(ResourceState.Undefined, RhiNative.ResourceState.Undefined)]
    [InlineData(ResourceState.RenderTarget, RhiNative.ResourceState.RenderTarget)]
    [InlineData(ResourceState.DepthStencil, RhiNative.ResourceState.DepthWrite)]
    [InlineData(ResourceState.ShaderRead, RhiNative.ResourceState.ShaderRead)]
    [InlineData(ResourceState.UnorderedAccess, RhiNative.ResourceState.UnorderedAccess)]
    [InlineData(ResourceState.CopySrc, RhiNative.ResourceState.CopySource)]
    [InlineData(ResourceState.CopyDst, RhiNative.ResourceState.CopyDest)]
    [InlineData(ResourceState.Present, RhiNative.ResourceState.Present)]
    [InlineData(ResourceState.IndirectRead, RhiNative.ResourceState.IndirectRead)]
    public void RenderGraphState_MapsToNativeState(
        ResourceState graphState,
        RhiNative.ResourceState nativeState)
    {
        Assert.Equal(
            nativeState,
            RenderGraphExecutor.ToNativeResourceState(graphState));
    }

    private sealed class TestPass : RenderPass
    {
        private readonly Action<RenderGraphBuilder> _setup;

        public TestPass(
            string name,
            Action<RenderGraphBuilder> setup)
        {
            Name = name;
            _setup = setup;
        }

        public override void Setup(RenderGraphBuilder builder)
            => _setup(builder);

        public override void Execute(
            ICommandSink sink,
            RenderGraphContext context)
        {
        }
    }
}
