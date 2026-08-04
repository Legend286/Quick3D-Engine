// SPDX-License-Identifier: MIT

using System;
using Engine.RenderGraph;
using Xunit;

namespace Engine.Game.Tests;

public sealed class RenderGraphCompilerTests
{
    [Fact]
    public void BuilderCreatedHandles_AreGraphLocalAndDeterministic()
    {
        RenderGraphCompiler compiler = new();
        RenderPlan first = compiler.Compile(new[] { new CreatedResourcePass() });
        RenderPlan second = compiler.Compile(new[] { new CreatedResourcePass() });

        ResourceHandle firstHandle = Assert.Single(first.ResourceDecls).Key;
        ResourceHandle secondHandle = Assert.Single(second.ResourceDecls).Key;

        Assert.Equal(new ResourceHandle(0x01000001), firstHandle);
        Assert.Equal(firstHandle, secondHandle);
    }

    [Fact]
    public void EquivalentDeclarations_ReuseImmutableTemplate()
    {
        RenderGraphCompiler compiler = new();
        RenderPlan first = compiler.Compile(new[] { new CreatedResourcePass() });
        RenderPlan second = compiler.Compile(new[] { new CreatedResourcePass() });

        Assert.Same(first.Template, second.Template);
        Assert.Equal(1, compiler.CachedTemplateCount);
        Assert.NotSame(first.Passes, second.Passes);
    }

    [Fact]
    public void DeclarationSnapshot_IsIndependentOfOriginalDescriptor()
    {
        RenderGraphCompiler compiler = new();
        var descriptor = new TextureDesc(64, 64)
        {
            MipLevels = 2,
        };
        RenderPlan plan = compiler.Compile(
            new[] { new CreatedResourcePass(descriptor) });

        TextureDesc snapshot =
            Assert.Single(plan.ResourceDecls).Value.Texture!;
        Assert.NotSame(descriptor, snapshot);
        Assert.Equal(64u, snapshot.Width);
        Assert.Equal(2u, snapshot.MipLevels);
    }

    [Fact]
    public void ClearCache_DropsTemplatesWithoutAffectingExistingPlan()
    {
        RenderGraphCompiler compiler = new();
        RenderPlan plan = compiler.Compile(new[] { new CreatedResourcePass() });

        compiler.ClearCache();

        Assert.Equal(0, compiler.CachedTemplateCount);
        Assert.Single(plan.ResourceDecls);
        Assert.Single(plan.BarriersPerPass);
    }

    private sealed class CreatedResourcePass : RenderPass
    {
        private readonly TextureDesc _descriptor;

        public CreatedResourcePass(TextureDesc? descriptor = null)
        {
            Name = "Created Resource";
            _descriptor = descriptor ?? new TextureDesc(64, 64);
        }

        public override void Setup(RenderGraphBuilder builder)
        {
            ResourceHandle texture = builder.CreateTexture(_descriptor);
            builder.Write(texture, ResourceState.RenderTarget);
        }

        public override void Execute(
            ICommandSink sink,
            RenderGraphContext context)
        {
        }
    }
}
