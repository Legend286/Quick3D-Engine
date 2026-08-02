// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.Assets;
using Engine.RHI;
using Xunit;

namespace Engine.Game.Tests;

/// <summary>Verifies the managed GPU animation contracts without requiring a native device.</summary>
public sealed class GpuAnimationContractTests
{
    [Fact]
    public void GpuAnimationRecords_MatchShaderFieldSizes()
    {
        Assert.Equal(48, Marshal.SizeOf<LocalTransformGpu>());
        Assert.Equal(32, Marshal.SizeOf<SkeletonAssetGpu>());
        Assert.Equal(16, Marshal.SizeOf<BoneMetadataGpu>());
        Assert.Equal(8, Marshal.SizeOf<HierarchyLevelGpu>());
        Assert.Equal(40, Marshal.SizeOf<AnimationClipGpu>());
        Assert.Equal(72, Marshal.SizeOf<GpuAnimatorState>());
        Assert.Equal(48, Marshal.SizeOf<SkinnedVertexOutputGpu>());
        Assert.Equal(48, Marshal.SizeOf<SkinWorkItemGpu>());
        Assert.Equal(36, Marshal.SizeOf<SkinnedMeshAssetGpu>());
        Assert.Equal(32, Marshal.SizeOf<AnimatedMeshInstanceGpu>());
    }

    [Fact]
    public void SkeletonValidation_RejectsHierarchyIndexOutsideBoneArray()
    {
        SkeletonAsset skeleton = new()
        {
            Bones = new[]
            {
                new BoneMetadataGpu { ParentIndex = -1 },
            },
            HierarchyLevels = new[]
            {
                new HierarchyLevelGpu { BoneIndexOffset = 0, BoneCount = 1 },
            },
            HierarchyBoneIndices = new[] { 1u },
            InverseBindMatrices = new[] { Matrix4x4.Identity },
            ReferencePose = new[] { LocalTransformGpu.Identity },
        };

        Assert.Throws<InvalidDataException>(skeleton.Validate);
    }

    [Fact]
    public void ClipValidation_RejectsMismatchedDenseSampleCount()
    {
        AnimationClipAsset clip = new()
        {
            Metadata = new AnimationClipGpu
            {
                FrameCount = 2,
                BoneCount = 1,
                SampleRate = 30,
                Duration = 1.0f,
            },
            Samples = new[] { LocalTransformGpu.Identity },
        };

        Assert.Throws<InvalidDataException>(clip.Validate);
    }

    [Fact]
    public void AnimatorComponent_CreateSetsActiveLoopingAndGeneration()
    {
        AnimatorComponent animator = AnimatorComponent.Create(4, 9);

        Assert.Equal(4u, animator.SkeletonId);
        Assert.Equal(9u, animator.BaseClipId);
        Assert.Equal(1.0f, animator.PlaybackRate);
        Assert.Equal(1u, animator.Generation);
        Assert.True((animator.Flags & AnimatorComponent.ActiveFlag) != 0);
        Assert.True((animator.Flags & (1u << 1)) != 0);
    }

    [Fact]
    public void AnimationAssetRegistry_RegistersValidatedAssets()
    {
        AnimationAssetRegistry.Clear();
        try
        {
            uint skeletonId = AnimationAssetRegistry.RegisterSkeleton(
                new SkeletonAsset
                {
                    Bones = new[]
                    {
                        new BoneMetadataGpu { ParentIndex = -1 },
                    },
                    HierarchyLevels = new[]
                    {
                        new HierarchyLevelGpu { BoneIndexOffset = 0, BoneCount = 1 },
                    },
                    HierarchyBoneIndices = new[] { 0u },
                    InverseBindMatrices = new[] { Matrix4x4.Identity },
                    ReferencePose = new[] { LocalTransformGpu.Identity },
                });
            uint clipId = AnimationAssetRegistry.RegisterClip(
                new AnimationClipAsset
                {
                    Metadata = new AnimationClipGpu
                    {
                        FrameCount = 1,
                        BoneCount = 1,
                        SampleRate = 30,
                        Duration = 1.0f,
                        SkeletonId = skeletonId,
                    },
                    Samples = new[] { LocalTransformGpu.Identity },
                });

            Assert.NotNull(AnimationAssetRegistry.GetSkeleton(skeletonId));
            Assert.NotNull(AnimationAssetRegistry.GetClip(clipId));
        }
        finally
        {
            AnimationAssetRegistry.Clear();
        }
    }
}
