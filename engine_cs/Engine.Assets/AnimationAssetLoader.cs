// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Engine.Assets;

/// <summary>Result of importing one JSON animation sidecar.</summary>
public sealed class AnimationAssetImportResult
{
    /// <summary>Gets the registered skeleton ID.</summary>
    public uint SkeletonId { get; init; }

    /// <summary>Gets the registered clip IDs keyed by source clip name.</summary>
    public IReadOnlyDictionary<string, uint> ClipIds { get; init; } =
        new Dictionary<string, uint>(StringComparer.Ordinal);

    /// <summary>Gets the imported skeleton used by editor debug drawing.</summary>
    public SkeletonAsset Skeleton { get; init; } = new();

    /// <summary>Gets the first clip ID, suitable as the initial animator clip.</summary>
    public uint FirstClipId { get; init; }
}

/// <summary>Loads the version 1 JSON animation sidecar format.</summary>
public static class AnimationAssetLoader
{
    private const int CurrentVersion = 1;

    /// <summary>
    /// Loads and registers an `.anim` file containing dense local-TRS clip
    /// samples and either an embedded skeleton or a sibling `.skel` reference.
    /// </summary>
    public static AnimationAssetImportResult Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Animation file not found: {path}");

        AnimationFileDefinition definition =
            JsonSerializer.Deserialize<AnimationFileDefinition>(
                File.ReadAllText(path))
            ?? throw new InvalidDataException("Failed to parse .anim");
        if (definition.Version != CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported .anim version {definition.Version}; expected {CurrentVersion}.");
        }
        AnimationSkeletonDefinition skeletonDefinition;
        if (definition.Skeleton.HasValue &&
            definition.Skeleton.Value.ValueKind == JsonValueKind.Object)
        {
            skeletonDefinition = definition.Skeleton.Value
                .Deserialize<AnimationSkeletonDefinition>()
                ?? throw new InvalidDataException(".anim contains an invalid skeleton.");
        }
        else
        {
            string? skeletonPath = definition.SkeletonPath;
            if (definition.Skeleton.HasValue &&
                definition.Skeleton.Value.ValueKind == JsonValueKind.String)
            {
                skeletonPath = definition.Skeleton.Value.GetString();
            }
            skeletonDefinition = LoadSkeletonDefinition(path, skeletonPath);
        }
        if (definition.Clips == null || definition.Clips.Length == 0)
            throw new InvalidDataException(".anim must contain at least one clip.");

        SkeletonAsset skeleton = BuildSkeleton(skeletonDefinition);
        var clipDefinitions = new List<AnimationClipDefinition>(definition.Clips.Length);
        var clipNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (AnimationClipDefinition clipDefinition in definition.Clips)
        {
            if (string.IsNullOrWhiteSpace(clipDefinition.Name))
                throw new InvalidDataException(".anim contains a clip without a name.");
            if (!clipNames.Add(clipDefinition.Name))
                throw new InvalidDataException(
                    $".anim contains duplicate clip '{clipDefinition.Name}'.");

            BuildClip(clipDefinition, 0, skeleton.Bones.Length).Validate();
            clipDefinitions.Add(clipDefinition);
        }

        uint skeletonId = AnimationAssetRegistry.RegisterSkeleton(skeleton);
        var clipIds = new Dictionary<string, uint>(StringComparer.Ordinal);
        uint firstClipId = 0;
        foreach (AnimationClipDefinition clipDefinition in clipDefinitions)
        {
            AnimationClipAsset clip = BuildClip(
                clipDefinition,
                skeletonId,
                skeleton.Bones.Length);
            uint clipId = AnimationAssetRegistry.RegisterClip(clip);
            clipIds.Add(clipDefinition.Name, clipId);
            if (firstClipId == 0)
                firstClipId = clipId;
        }

        return new AnimationAssetImportResult
        {
            SkeletonId = skeletonId,
            ClipIds = clipIds,
            Skeleton = skeleton,
            FirstClipId = firstClipId,
        };
    }

    /// <summary>Loads and registers a standalone `.skel` skeleton asset.</summary>
    public static uint LoadSkeleton(string path)
    {
        AnimationSkeletonDefinition definition = LoadSkeletonDefinition(path, null);
        return AnimationAssetRegistry.RegisterSkeleton(BuildSkeleton(definition));
    }

    private static AnimationSkeletonDefinition LoadSkeletonDefinition(
        string sourcePath,
        string? referencedPath)
    {
        string path = referencedPath == null
            ? sourcePath
            : Path.Combine(
                Path.GetDirectoryName(sourcePath) ?? string.Empty,
                referencedPath);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Skeleton file not found: {path}");

        AnimationSkeletonDefinition definition;
        try
        {
            definition =
                JsonSerializer.Deserialize<AnimationSkeletonDefinition>(
                    File.ReadAllText(path))
                ?? throw new InvalidDataException(
                    $"Failed to parse skeleton file: {path}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Failed to parse skeleton file {path}: {exception.Message}",
                exception);
        }
        if (definition.Version != CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported .skel version {definition.Version}; " +
                $"expected {CurrentVersion}. File: {path}");
        }
        return definition;
    }

    private static SkeletonAsset BuildSkeleton(
        AnimationSkeletonDefinition definition)
    {
        if (definition.Bones == null || definition.Bones.Length == 0)
            throw new InvalidDataException(".anim skeleton must contain bones.");

        var bones = new BoneMetadataGpu[definition.Bones.Length];
        var referencePose = new LocalTransformGpu[definition.Bones.Length];
        for (int index = 0; index < definition.Bones.Length; ++index)
        {
            AnimationBoneDefinition bone = definition.Bones[index];
            int parent = bone.Parent;
            if (parent < -1 || parent >= definition.Bones.Length)
                throw new InvalidDataException(
                    $".anim bone {index} has an invalid parent index.");

            bones[index] = new BoneMetadataGpu
            {
                ParentIndex = parent,
                NameHash = StableNameHash(bone.Name ?? $"bone_{index}"),
            };
            referencePose[index] = ToLocalTransform(bone);
        }

        uint rootBone = definition.RootBone;
        if (rootBone >= bones.Length)
            throw new InvalidDataException(".anim root bone is outside the bone array.");

        var depths = new uint[bones.Length];
        var visitState = new byte[bones.Length];
        for (int index = 0; index < bones.Length; ++index)
        {
            depths[index] = ComputeDepth(index, bones, depths, visitState);
            bones[index].HierarchyDepth = depths[index];
        }

        uint maxDepth = 0;
        for (int index = 0; index < depths.Length; ++index)
            maxDepth = Math.Max(maxDepth, depths[index]);

        var hierarchyLevels = new HierarchyLevelGpu[maxDepth + 1];
        var hierarchyIndices = new List<uint>(bones.Length);
        for (uint depth = 0; depth <= maxDepth; ++depth)
        {
            uint offset = checked((uint)hierarchyIndices.Count);
            for (uint index = 0; index < bones.Length; ++index)
            {
                if (depths[index] == depth)
                    hierarchyIndices.Add(index);
            }
            hierarchyLevels[depth] = new HierarchyLevelGpu
            {
                BoneIndexOffset = offset,
                BoneCount = checked((uint)hierarchyIndices.Count) - offset,
            };
        }

        Matrix4x4[] inverseBind =
            new Matrix4x4[bones.Length];
        if (definition.InverseBindMatrices == null ||
            definition.InverseBindMatrices.Length == 0)
        {
            Array.Fill(inverseBind, Matrix4x4.Identity);
        }
        else
        {
            if (definition.InverseBindMatrices.Length != bones.Length)
                throw new InvalidDataException(
                    ".anim inverse-bind count does not match bone count.");
            for (int index = 0; index < inverseBind.Length; ++index)
                inverseBind[index] = ToMatrix4x4(
                    definition.InverseBindMatrices[index]);
        }

        return new SkeletonAsset
        {
            Bones = bones,
            HierarchyLevels = hierarchyLevels,
            HierarchyBoneIndices = hierarchyIndices.ToArray(),
            InverseBindMatrices = inverseBind,
            ReferencePose = referencePose,
            RootBoneIndex = rootBone,
        };
    }

    private static AnimationClipAsset BuildClip(
        AnimationClipDefinition definition,
        uint skeletonId,
        int boneCount)
    {
        if (definition.Frames == null || definition.Frames.Length == 0)
            throw new InvalidDataException(
                $"Animation clip '{definition.Name}' has no frames.");
        if (definition.SampleRate <= 0)
            throw new InvalidDataException(
                $"Animation clip '{definition.Name}' has an invalid sample rate.");

        var samples = new LocalTransformGpu[
            checked(definition.Frames.Length * boneCount)];
        for (int frameIndex = 0; frameIndex < definition.Frames.Length; ++frameIndex)
        {
            AnimationBoneDefinition[] frame = definition.Frames[frameIndex] ??
                throw new InvalidDataException(
                    $"Animation clip '{definition.Name}' contains a null frame.");
            if (frame.Length != boneCount)
                throw new InvalidDataException(
                    $"Animation clip '{definition.Name}' frame {frameIndex} does not match bone count.");
            for (int boneIndex = 0; boneIndex < boneCount; ++boneIndex)
            {
                samples[frameIndex * boneCount + boneIndex] =
                    ToLocalTransform(frame[boneIndex]);
            }
        }

        float duration = definition.Duration > 0.0f
            ? definition.Duration
            : MathF.Max(
                1.0f / definition.SampleRate,
                (definition.Frames.Length - 1) /
                (float)definition.SampleRate);
        return new AnimationClipAsset
        {
            Metadata = new AnimationClipGpu
            {
                FrameCount = checked((uint)definition.Frames.Length),
                BoneCount = checked((uint)boneCount),
                SampleRate = checked((uint)definition.SampleRate),
                Duration = duration,
                Flags = definition.Looping
                    ? (uint)AnimationClipFlags.Looping
                    : 0u,
                SkeletonId = skeletonId,
            },
            Samples = samples,
        };
    }

    private static uint ComputeDepth(
        int index,
        BoneMetadataGpu[] bones,
        uint[] depths,
        byte[] visitState)
    {
        if (visitState[index] == 2)
            return depths[index];
        if (visitState[index] == 1)
            throw new InvalidDataException(".anim skeleton hierarchy contains a cycle.");

        visitState[index] = 1;
        int parent = bones[index].ParentIndex;
        uint depth = parent < 0
            ? 0u
            : checked(ComputeDepth(parent, bones, depths, visitState) + 1u);
        depths[index] = depth;
        visitState[index] = 2;
        return depth;
    }

    private static LocalTransformGpu ToLocalTransform(
        AnimationBoneDefinition definition)
    {
        float[] translation = definition.Translation ?? Array.Empty<float>();
        float[] rotation = definition.Rotation ?? Array.Empty<float>();
        float[] scale = definition.Scale ?? Array.Empty<float>();
        Quaternion quaternion = rotation.Length >= 4
            ? new Quaternion(rotation[0], rotation[1], rotation[2], rotation[3])
            : Quaternion.Identity;
        Vector3 position = translation.Length >= 3
            ? new Vector3(translation[0], translation[1], translation[2])
            : Vector3.Zero;
        Vector3 scaleValue = scale.Length >= 3
            ? new Vector3(scale[0], scale[1], scale[2])
            : Vector3.One;
        return LocalTransformGpu.Create(quaternion, position, scaleValue);
    }

    private static Matrix4x4 ToMatrix4x4(float[]? values)
    {
        if (values == null || values.Length != 16)
            throw new InvalidDataException(
                ".anim inverse-bind matrices must contain sixteen values.");
        return new Matrix4x4(
            values[0], values[1], values[2], values[3],
            values[4], values[5], values[6], values[7],
            values[8], values[9], values[10], values[11],
            values[12], values[13], values[14], values[15]);
    }

    private static uint StableNameHash(string value)
    {
        uint hash = 2166136261u;
        foreach (char character in value)
        {
            hash ^= character;
            hash *= 16777619u;
        }
        return hash;
    }

    private sealed class AnimationFileDefinition
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("skeleton")]
        public JsonElement? Skeleton { get; set; }

        [JsonPropertyName("clips")]
        public AnimationClipDefinition[]? Clips { get; set; }

        [JsonPropertyName("skeleton_path")]
        public string? SkeletonPath { get; set; }
    }

    private sealed class AnimationSkeletonDefinition
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("root_bone")]
        public uint RootBone { get; set; }

        [JsonPropertyName("bones")]
        public AnimationBoneDefinition[]? Bones { get; set; }

        [JsonPropertyName("inverse_bind_matrices")]
        public float[][]? InverseBindMatrices { get; set; }
    }

    private sealed class AnimationClipDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("sample_rate")]
        public int SampleRate { get; set; }

        [JsonPropertyName("duration")]
        public float Duration { get; set; }

        [JsonPropertyName("looping")]
        public bool Looping { get; set; } = true;

        [JsonPropertyName("frames")]
        public AnimationBoneDefinition[][]? Frames { get; set; }
    }

    private sealed class AnimationBoneDefinition
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("parent")]
        public int Parent { get; set; } = -1;

        [JsonPropertyName("translation")]
        public float[]? Translation { get; set; }

        [JsonPropertyName("rotation")]
        public float[]? Rotation { get; set; }

        [JsonPropertyName("scale")]
        public float[]? Scale { get; set; }
    }
}
