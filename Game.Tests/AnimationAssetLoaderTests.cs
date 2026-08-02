// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Numerics;
using Engine.Assets;
using Xunit;

namespace Engine.Game.Tests;

/// <summary>Verifies JSON animation sidecar import without requiring a GPU device.</summary>
public sealed class AnimationAssetLoaderTests
{
    [Fact]
    public void Load_RegistersSkeletonAndFirstDenseClip()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"quick3d-{Guid.NewGuid():N}.anim");
        File.WriteAllText(path, """
        {
          "version": 1,
          "skeleton": {
            "root_bone": 0,
            "bones": [
              { "name": "root", "parent": -1,
                "translation": [0, 0, 0],
                "rotation": [0, 0, 0, 1],
                "scale": [1, 1, 1] },
              { "name": "hand", "parent": 0,
                "translation": [0, 1, 0],
                "rotation": [0, 0, 0, 1],
                "scale": [1, 1, 1] }
            ]
          },
          "clips": [
            {
              "name": "idle",
              "sample_rate": 30,
              "duration": 1,
              "looping": true,
              "frames": [
                [
                  { "translation": [0, 0, 0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1] },
                  { "translation": [0, 1, 0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1] }
                ]
              ]
            }
          ]
        }
        """);

        AnimationAssetRegistry.Clear();
        try
        {
            AnimationAssetImportResult result =
                AnimationAssetLoader.Load(path);

            Assert.NotEqual(0u, result.SkeletonId);
            Assert.NotEqual(0u, result.FirstClipId);
            Assert.Single(result.ClipIds);
            Assert.Equal(2, result.Skeleton.Bones.Length);
            Assert.Equal(2, result.Skeleton.HierarchyBoneIndices.Length);
            Assert.Equal(0u, result.Skeleton.HierarchyBoneIndices[0]);
            Assert.Equal(1u, result.Skeleton.HierarchyBoneIndices[1]);

            AnimationClipAsset? clip =
                AnimationAssetRegistry.GetClip(result.FirstClipId);
            Assert.NotNull(clip);
            Assert.Equal(2u, clip!.Metadata.BoneCount);
            Assert.Equal(2, clip.Samples.Length);
            Assert.Equal(
                new Vector4(1.0f, 1.0f, 1.0f, 0.0f),
                clip.Samples[0].Scale);
        }
        finally
        {
            AnimationAssetRegistry.Clear();
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_SiblingSkeletonReference_UsesCookShapedSkelFile()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"quick3d-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string skeletonPath = Path.Combine(directory, "spider.skel");
        string animationPath = Path.Combine(directory, "spider.anim");
        File.WriteAllText(skeletonPath, """
        {
          "version": 1,
          "root_bone": 0,
          "bones": [
            { "name": "root", "parent": -1,
              "translation": [0, 0, 0],
              "rotation": [0, 0, 0, 1],
              "scale": [1, 1, 1] },
            { "name": "leg", "parent": 0,
              "translation": [0, 1, 0],
              "rotation": [0, 0, 0, 1],
              "scale": [1, 1, 1] }
          ],
          "inverse_bind_matrices": [
            [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1],
            [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
          ]
        }
        """);
        File.WriteAllText(animationPath, """
        {
          "version": 1,
          "skeleton_path": "spider.skel",
          "clips": [
            {
              "name": "walk",
              "sample_rate": 30,
              "duration": 1,
              "looping": true,
              "frames": [
                [
                  { "translation": [0, 0, 0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1] },
                  { "translation": [0, 2, 0], "rotation": [0, 0, 0, 1], "scale": [1, 1, 1] }
                ]
              ]
            }
          ]
        }
        """);

        AnimationAssetRegistry.Clear();
        try
        {
            AnimationAssetImportResult result =
                AnimationAssetLoader.Load(animationPath);

            Assert.NotEqual(0u, result.SkeletonId);
            Assert.Equal(2, result.Skeleton.Bones.Length);
            Assert.Equal(2, result.Skeleton.InverseBindMatrices.Length);
            Assert.Equal(0, result.Skeleton.Bones[1].ParentIndex);
            Assert.Single(result.ClipIds);
            Assert.True(result.ClipIds.ContainsKey("walk"));
        }
        finally
        {
            AnimationAssetRegistry.Clear();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_RejectsCyclicSkeleton()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"quick3d-{Guid.NewGuid():N}.anim");
        File.WriteAllText(path, """
        {
          "version": 1,
          "skeleton": {
            "root_bone": 0,
            "bones": [
              { "name": "a", "parent": 1 },
              { "name": "b", "parent": 0 }
            ]
          },
          "clips": [
            {
              "name": "idle",
              "sample_rate": 30,
              "duration": 1,
              "frames": [
                [ {}, {} ]
              ]
            }
          ]
        }
        """);

        AnimationAssetRegistry.Clear();
        try
        {
            Assert.Throws<InvalidDataException>(
                () => AnimationAssetLoader.Load(path));
        }
        finally
        {
            AnimationAssetRegistry.Clear();
            File.Delete(path);
        }
    }
}
