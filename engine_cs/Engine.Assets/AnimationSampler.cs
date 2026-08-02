// SPDX-License-Identifier: MIT
using System;
using System.Numerics;

namespace Engine.Assets;

/// <summary>
/// CPU mirror of the GPU clip sampler in
/// <c>Content/shaders/animation_gpu.slang</c>. The debug skeleton overlay
/// uses it so the drawn bones match the pose the renderer actually skins.
/// </summary>
public static class AnimationSampler
{
    /// <summary>Samples one bone's local transform from a dense clip.</summary>
    /// <remarks>Behaviour mirrors <c>SampleClip</c> in the animation shader.</remarks>
    public static LocalTransformGpu SampleClip(
        AnimationClipAsset clip,
        float time,
        uint boneIndex,
        bool looping,
        LocalTransformGpu fallback)
    {
        AnimationClipGpu metadata = clip.Metadata;
        if (metadata.FrameCount == 0u ||
            metadata.BoneCount == 0u ||
            boneIndex >= metadata.BoneCount)
        {
            return fallback;
        }

        float framePosition = MathF.Max(time, 0.0f) * metadata.SampleRate;
        float floor = MathF.Floor(framePosition);
        uint frame0 = ResolveFrame(
            (uint)floor, metadata.FrameCount, looping);
        uint frame1 = ResolveFrame(
            frame0 + 1u, metadata.FrameCount, looping);
        float alpha = framePosition - floor;
        LocalTransformGpu first =
            clip.Samples[frame0 * metadata.BoneCount + boneIndex];
        LocalTransformGpu second =
            clip.Samples[frame1 * metadata.BoneCount + boneIndex];
        return InterpolateTransform(first, second, alpha);
    }

    /// <summary>Mirrors <c>ResolveFrame</c> in the animation shader.</summary>
    private static uint ResolveFrame(
        uint frame,
        uint frameCount,
        bool looping)
        => frameCount == 0u
            ? 0u
            : looping
                ? frame % frameCount
                : Math.Min(frame, frameCount - 1u);

    /// <summary>Mirrors <c>InterpolateTransform</c> in the animation shader,
    /// including shortest-path quaternion handling.</summary>
    private static LocalTransformGpu InterpolateTransform(
        LocalTransformGpu first,
        LocalTransformGpu second,
        float alpha)
    {
        Vector4 secondRotation = second.Rotation;
        if (Vector4.Dot(first.Rotation, secondRotation) < 0.0f)
            secondRotation = -secondRotation;

        Vector3 firstTranslation = new(first.Translation.X, first.Translation.Y, first.Translation.Z);
        Vector3 secondTranslation = new(second.Translation.X, second.Translation.Y, second.Translation.Z);
        Vector3 firstScale = new(first.Scale.X, first.Scale.Y, first.Scale.Z);
        Vector3 secondScale = new(second.Scale.X, second.Scale.Y, second.Scale.Z);
        return new LocalTransformGpu
        {
            Rotation = NormalizeQuaternion(
                Vector4.Lerp(first.Rotation, secondRotation, alpha)),
            Translation = new Vector4(
                Vector3.Lerp(firstTranslation, secondTranslation, alpha),
                0.0f),
            Scale = new Vector4(
                Vector3.Lerp(firstScale, secondScale, alpha),
                0.0f),
        };
    }

    /// <summary>Mirrors <c>NormalizeQuaternion</c> in the animation shader.</summary>
    private static Vector4 NormalizeQuaternion(Vector4 value)
    {
        float lengthSquared =
            value.X * value.X + value.Y * value.Y +
            value.Z * value.Z + value.W * value.W;
        return lengthSquared > 1.0e-8f
            ? value * (1.0f / MathF.Sqrt(lengthSquared))
            : new Vector4(0.0f, 0.0f, 0.0f, 1.0f);
    }
}
