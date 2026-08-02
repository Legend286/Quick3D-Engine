// SPDX-License-Identifier: MIT
using System;
using System.Numerics;
using Engine.Assets;
using Xunit;

namespace Engine.Game.Tests;

/// <summary>Verifies the CPU animation sampler used by the skeleton debug overlay.</summary>
public sealed class AnimationSamplerTests
{
    private static AnimationClipAsset MakeClip(
        uint frameCount,
        bool looping,
        params LocalTransformGpu[] frames)
    {
        return new AnimationClipAsset
        {
            Metadata = new AnimationClipGpu
            {
                FrameCount = frameCount,
                BoneCount = 1,
                SampleRate = 30,
                Duration = 1.0f,
                Flags = looping ? (uint)AnimationClipFlags.Looping : 0u,
                SkeletonId = 1,
            },
            Samples = frames,
        };
    }

    private static LocalTransformGpu Translate(float y)
        => LocalTransformGpu.Create(
            Quaternion.Identity,
            new Vector3(0.0f, y, 0.0f),
            Vector3.One);

    private static LocalTransformGpu RotateZ(float degrees)
    {
        float halfRadians = degrees * MathF.PI / 360.0f;
        return LocalTransformGpu.Create(
            new Quaternion(0.0f, 0.0f, MathF.Sin(halfRadians), MathF.Cos(halfRadians)),
            Vector3.Zero,
            Vector3.One);
    }

    [Fact]
    public void SampleClip_LinearInterpolatesTranslations()
    {
        AnimationClipAsset clip = MakeClip(
            2,
            looping: false,
            Translate(0.0f),
            Translate(1.0f));

        // t = 1/60s at 30fps → framePosition 0.5 → alpha 0.5 between frames.
        LocalTransformGpu atHalf = AnimationSampler.SampleClip(
            clip, 1.0f / 60.0f, 0, looping: false, LocalTransformGpu.Identity);
        Assert.Equal(0.5f, atHalf.Translation.Y, precision: 4);

        LocalTransformGpu atStart = AnimationSampler.SampleClip(
            clip, 0.0f, 0, looping: false, LocalTransformGpu.Identity);
        Assert.Equal(0.0f, atStart.Translation.Y, precision: 4);
    }

    [Fact]
    public void SampleClip_LoopingWrapsFrameIndexPastEnd()
    {
        AnimationClipAsset clip = MakeClip(
            2,
            looping: true,
            Translate(0.0f),
            Translate(1.0f));

        // t = 2/30s → framePosition 2 → frame0 wraps 2 % 2 = 0, alpha 0.
        LocalTransformGpu afterEnd = AnimationSampler.SampleClip(
            clip, 2.0f / 30.0f, 0, looping: true, LocalTransformGpu.Identity);
        Assert.Equal(0.0f, afterEnd.Translation.Y, precision: 4);
    }

    [Fact]
    public void SampleClip_ShortestPathFlipsQuaternion()
    {
        // +120° and -120° quaternions have dot < 0; naive lerp would swing
        // through identity (240° arc). The flip routes through the short arc
        // whose midpoint is the 180°-about-Z quaternion.
        AnimationClipAsset clip = MakeClip(
            2,
            looping: false,
            RotateZ(120.0f),
            RotateZ(-120.0f));

        LocalTransformGpu mid = AnimationSampler.SampleClip(
            clip, 1.0f / 60.0f, 0, looping: false, LocalTransformGpu.Identity);
        Assert.True(
            MathF.Abs(mid.Rotation.Z) > 0.99f,
            $"z={mid.Rotation.Z} should be ~1 (short arc through 180°)");
        Assert.True(
            MathF.Abs(mid.Rotation.W) < 0.05f,
            $"w={mid.Rotation.W} should be ~0 (short arc through 180°)");
    }

    [Fact]
    public void SampleClip_EmptyClipReturnsFallback()
    {
        AnimationClipAsset clip = MakeClip(0, looping: true);
        LocalTransformGpu fallback = Translate(42.0f);
        LocalTransformGpu result = AnimationSampler.SampleClip(
            clip, 0.5f, 0, looping: true, fallback);
        Assert.Equal(42.0f, result.Translation.Y, precision: 4);
    }
}
