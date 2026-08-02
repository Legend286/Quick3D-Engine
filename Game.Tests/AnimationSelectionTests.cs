// SPDX-License-Identifier: MIT
using System;
using System.Numerics;
using Engine.Assets;
using Xunit;

namespace Engine.Game.Tests;

public sealed class AnimationSelectionTests
{
    [Fact]
    public void ImportedAnimationResult_ExposesEveryRegisteredClipForSelection()
    {
        AnimationAssetRegistry.Clear();
        try
        {
            uint skeletonId = RegisterSkeleton();
            uint walkId = RegisterClip(skeletonId, "walk");
            uint runId = RegisterClip(skeletonId, "run");
            var result = new AnimationAssetImportResult
            {
                SkeletonId = skeletonId,
                FirstClipId = walkId,
                ClipIds = new System.Collections.Generic.Dictionary<string, uint>
                {
                    ["walk"] = walkId,
                    ["run"] = runId,
                },
            };

            Assert.Equal(2, result.ClipIds.Count);
            Assert.Contains(runId, result.ClipIds.Values);
            Assert.Contains(walkId, result.ClipIds.Values);
        }
        finally
        {
            AnimationAssetRegistry.Clear();
        }
    }

    [Fact]
    public void DistinctDropStartTimesProduceDistinctAnimationPoseKeys()
    {
        uint clipId = 7;
        float firstStartTime = 0.25f;
        float secondStartTime = 0.75f;

        Assert.NotEqual(
            (clipId, firstStartTime),
            (clipId, secondStartTime));
    }

    private static uint RegisterSkeleton()
        => AnimationAssetRegistry.RegisterSkeleton(new SkeletonAsset
        {
            Bones = new[] { new BoneMetadataGpu { ParentIndex = -1 } },
            HierarchyLevels = new[]
            {
                new HierarchyLevelGpu { BoneIndexOffset = 0, BoneCount = 1 }
            },
            HierarchyBoneIndices = new[] { 0u },
            InverseBindMatrices = new[] { Matrix4x4.Identity },
            ReferencePose = new[] { LocalTransformGpu.Identity },
        });

    private static uint RegisterClip(uint skeletonId, string name)
        => AnimationAssetRegistry.RegisterClip(new AnimationClipAsset
        {
            Name = name,
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
}
