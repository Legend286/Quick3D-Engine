// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Engine.Assets;

/// <summary>GPU-compatible local transform stored by dense animation clips.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct LocalTransformGpu
{
    public Vector4 Rotation;
    public Vector4 Translation;
    public Vector4 Scale;

    /// <summary>Creates a transform using engine-native quaternion and TRS conventions.</summary>
    public static LocalTransformGpu Create(
        Quaternion rotation,
        Vector3 translation,
        Vector3 scale)
        => new()
        {
            Rotation = new Vector4(rotation.X, rotation.Y, rotation.Z, rotation.W),
            Translation = new Vector4(translation, 0.0f),
            Scale = new Vector4(scale, 0.0f),
        };

    /// <summary>Returns the identity local transform.</summary>
    public static LocalTransformGpu Identity => Create(
        Quaternion.Identity,
        Vector3.Zero,
        Vector3.One);
}

/// <summary>Immutable GPU metadata record describing one skeleton asset.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SkeletonAssetGpu
{
    public uint BoneOffset;
    public uint BoneCount;
    public uint HierarchyLevelOffset;
    public uint HierarchyLevelCount;
    public uint InverseBindOffset;
    public uint ReferencePoseOffset;
    public uint RootBoneIndex;
    public uint Flags;
}

/// <summary>GPU metadata for one skeleton bone.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct BoneMetadataGpu
{
    public int ParentIndex;
    public uint HierarchyDepth;
    public uint NameHash;
    public uint Flags;
}

/// <summary>Range of bones belonging to one hierarchy depth.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct HierarchyLevelGpu
{
    public uint BoneIndexOffset;
    public uint BoneCount;
}

/// <summary>Dense GPU animation clip metadata.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AnimationClipGpu
{
    public uint SampleOffset;
    public uint FrameCount;
    public uint BoneCount;
    public uint SampleRate;
    public float Duration;
    public uint RootMotionOffset;
    public uint EventOffset;
    public uint EventCount;
    public uint Flags;
    public uint SkeletonId;
}

/// <summary>Persistent GPU animator state using stable per-category asset IDs.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct GpuAnimatorState
{
    public uint SkeletonId;
    public uint EntityId;
    public uint BaseClipId;
    public uint TargetClipId;
    public float BaseTime;
    public float TargetTime;
    public float PlaybackRate;
    public float TransitionTime;
    public float TransitionDuration;
    public float TransitionWeight;
    public uint LayerOffset;
    public uint LayerCount;
    public uint Flags;
    public uint Generation;
    public uint OutputPoseOffset;
    public uint OutputMatrixOffset;
    public uint CurrentSkinnedVertexOffset;
    public uint PreviousSkinnedVertexOffset;
}

/// <summary>Flags stored in <see cref="AnimationClipGpu.Flags"/>.</summary>
[Flags]
public enum AnimationClipFlags : uint
{
    Looping = 1u << 0,
    HasRootMotion = 1u << 1,
    HasScaleTracks = 1u << 2,
    Additive = 1u << 3,
    InPlace = 1u << 4,
    HasEvents = 1u << 5,
}

/// <summary>Flags stored in <see cref="GpuAnimatorState.Flags"/>.</summary>
[Flags]
public enum GpuAnimatorFlags : uint
{
    Active = 1u << 0,
    Looping = 1u << 1,
    InTransition = 1u << 2,
    Paused = 1u << 3,
    UseDualQuaternion = 1u << 4,
    NeedsSkinning = 1u << 5,
    NeedsBlasUpdate = 1u << 6,
    ResetHistory = 1u << 7,
}

/// <summary>Classifies whether a mesh can reuse immutable geometry or needs a per-instance deformation stream.</summary>
public enum MeshDeformationKind : uint
{
    Static = 0,
    Deforming = 1,
}

/// <summary>Source vertex consumed by the compute-skinning contract.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SkinSourceVertexGpu
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Texcoord;
    public Vector4 Tangent;
    public Vector4 BoneIndices;
    public Vector4 BoneWeights;
}

/// <summary>Dynamic vertex stream emitted by compute skinning in the visibility Vertex ABI.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SkinnedVertexOutputGpu
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 Texcoord;
    public Vector4 Tangent;
}

/// <summary>GPU metadata for a skinned mesh asset.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SkinnedMeshAssetGpu
{
    public uint SkeletonId;
    public uint SourceVertexOffset;
    public uint VertexCount;
    public uint IndexOffset;
    public uint IndexCount;
    public uint GeometryOffset;
    public uint GeometryCount;
    public uint MaxInfluences;
    public uint Flags;
}

/// <summary>One compute-skinning dispatch item for an entity-local mesh stream.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SkinWorkItemGpu
{
    public ulong SourceVertices;
    public ulong OutputVertices;
    public ulong SkinMatrices;
    public uint VertexCount;
    public uint Pad;
    public Vector3 OutputOffset;
    public uint OutputPad;
}

/// <summary>GPU metadata connecting an animator to one skinned mesh.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct AnimatedMeshInstanceGpu
{
    public uint AnimatorIndex;
    public uint MeshAssetId;
    public uint SceneInstanceId;
    public uint MaterialOverrideOffset;
    public uint CurrentVertexOffset;
    public uint PreviousVertexOffset;
    public uint BlasHandleIndex;
    public uint Flags;
}

/// <summary>Validated immutable skeleton data awaiting GPU upload.</summary>
public sealed class SkeletonAsset
{
    /// <summary>Gets the local bone metadata in stable skeleton order.</summary>
    public BoneMetadataGpu[] Bones { get; init; } = Array.Empty<BoneMetadataGpu>();

    /// <summary>Gets hierarchy levels ordered from roots to leaves.</summary>
    public HierarchyLevelGpu[] HierarchyLevels { get; init; } = Array.Empty<HierarchyLevelGpu>();

    /// <summary>Gets the flattened bone indices referenced by hierarchy levels.</summary>
    public uint[] HierarchyBoneIndices { get; init; } = Array.Empty<uint>();

    /// <summary>Gets one inverse-bind matrix per bone.</summary>
    public Matrix4x4[] InverseBindMatrices { get; init; } = Array.Empty<Matrix4x4>();

    /// <summary>Gets the reference local pose, with one transform per bone.</summary>
    public LocalTransformGpu[] ReferencePose { get; init; } = Array.Empty<LocalTransformGpu>();

    /// <summary>Gets the root bone index.</summary>
    public uint RootBoneIndex { get; init; }

    /// <summary>Validates hierarchy, pose, and inverse-bind cardinalities.</summary>
    public void Validate()
    {
        if (Bones.Length == 0)
            throw new InvalidDataException("Skeleton must contain at least one bone.");
        if (ReferencePose.Length != Bones.Length)
            throw new InvalidDataException("Skeleton reference pose does not match bone count.");
        if (InverseBindMatrices.Length != Bones.Length)
            throw new InvalidDataException("Skeleton inverse-bind count does not match bone count.");
        if (RootBoneIndex >= Bones.Length)
            throw new InvalidDataException("Skeleton root bone is outside the bone array.");
        foreach (BoneMetadataGpu bone in Bones)
        {
            if (bone.ParentIndex < -1 || bone.ParentIndex >= Bones.Length)
                throw new InvalidDataException("Skeleton contains an invalid parent index.");
        }
        foreach (HierarchyLevelGpu level in HierarchyLevels)
        {
            if ((ulong)level.BoneIndexOffset + level.BoneCount > (ulong)HierarchyBoneIndices.Length)
                throw new InvalidDataException("Skeleton hierarchy level exceeds its index array.");
            for (uint index = 0; index < level.BoneCount; ++index)
            {
                if (HierarchyBoneIndices[level.BoneIndexOffset + index] >= Bones.Length)
                    throw new InvalidDataException("Skeleton hierarchy references an invalid bone.");
            }
        }
    }
}

/// <summary>Dense, frame-major animation clip data.</summary>
public sealed class AnimationClipAsset
{
    /// <summary>Gets the authored clip name used by editor and scene selection.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets clip metadata used by the GPU sampler.</summary>
    public AnimationClipGpu Metadata { get; init; }

    /// <summary>Gets frame-major local transforms: frame, then bone.</summary>
    public LocalTransformGpu[] Samples { get; init; } = Array.Empty<LocalTransformGpu>();

    /// <summary>Validates clip dimensions and sample cardinality.</summary>
    public void Validate()
    {
        if (Metadata.FrameCount == 0 || Metadata.BoneCount == 0)
            throw new InvalidDataException("Animation clip must contain frames and bones.");
        if (Metadata.SampleRate == 0 || Metadata.Duration <= 0.0f ||
            float.IsNaN(Metadata.Duration) || float.IsInfinity(Metadata.Duration))
            throw new InvalidDataException("Animation clip has invalid timing metadata.");
        ulong expectedSamples = (ulong)Metadata.FrameCount * Metadata.BoneCount;
        if ((ulong)Samples.Length != expectedSamples)
            throw new InvalidDataException("Animation sample count does not match clip dimensions.");
    }
}

/// <summary>Stable process-local IDs for immutable animation assets.</summary>
public static class AnimationAssetRegistry
{
    private static readonly object Sync = new();
    private static readonly System.Collections.Generic.Dictionary<uint, SkeletonAsset> Skeletons = new();
    private static readonly System.Collections.Generic.Dictionary<uint, AnimationClipAsset> Clips = new();
    private static uint _nextSkeletonId = 1;
    private static uint _nextClipId = 1;

    /// <summary>Registers and validates a skeleton, returning its stable ID.</summary>
    public static uint RegisterSkeleton(SkeletonAsset skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        skeleton.Validate();
        lock (Sync)
        {
            uint id = _nextSkeletonId++;
            Skeletons.Add(id, skeleton);
            return id;
        }
    }

    /// <summary>Registers and validates an animation clip, returning its stable ID.</summary>
    public static uint RegisterClip(AnimationClipAsset clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        clip.Validate();
        lock (Sync)
        {
            uint id = _nextClipId++;
            Clips.Add(id, clip);
            return id;
        }
    }

    /// <summary>Looks up a skeleton by stable ID.</summary>
    public static SkeletonAsset? GetSkeleton(uint id)
    {
        lock (Sync) return Skeletons.TryGetValue(id, out SkeletonAsset? value) ? value : null;
    }

    /// <summary>Looks up a clip by stable ID.</summary>
    public static AnimationClipAsset? GetClip(uint id)
    {
        lock (Sync) return Clips.TryGetValue(id, out AnimationClipAsset? value) ? value : null;
    }

    /// <summary>Returns clips that can be selected by an animator for one skeleton.</summary>
    public static IReadOnlyList<(uint Id, string Name)> GetClipsForSkeleton(uint skeletonId)
    {
        lock (Sync)
        {
            var result = new System.Collections.Generic.List<(uint Id, string Name)>();
            foreach (var entry in Clips)
            {
                if (entry.Value.Metadata.SkeletonId == skeletonId)
                    result.Add((entry.Key, entry.Value.Name));
            }
            result.Sort((left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
            return result;
        }
    }

    /// <summary>Gets a stable snapshot for GPU upload.</summary>
    public static (IReadOnlyDictionary<uint, SkeletonAsset> Skeletons, IReadOnlyDictionary<uint, AnimationClipAsset> Clips) Snapshot()
    {
        lock (Sync)
        {
            return (new System.Collections.Generic.Dictionary<uint, SkeletonAsset>(Skeletons),
                new System.Collections.Generic.Dictionary<uint, AnimationClipAsset>(Clips));
        }
    }

    /// <summary>Clears process-local registrations after GPU work is idle.</summary>
    public static void Clear()
    {
        lock (Sync)
        {
            Skeletons.Clear();
            Clips.Clear();
            _nextSkeletonId = 1;
            _nextClipId = 1;
        }
    }
}
