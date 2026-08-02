// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.Assets;
using Engine.CBindings;
using Engine.RHI;
using Engine.RenderGraph;
using Engine.Scene;

namespace Engine.Renderer;

/// <summary>Runs ordered GPU animation-matrix generation followed by vertex skinning.</summary>
/// <remarks>
/// The pass is compiled into every raster plan so shader and graph resources are
/// ready before animated content appears. It remains dormant when the current
/// world has no valid animated deforming work. Static scene geometry remains on
/// the existing visibility path.
/// </remarks>
internal sealed class GpuAnimationPass : RenderPass, IDisposable
{
    private const int PoseBufferCount = 3;
    private const uint EmptyBufferBytes = 16;
    private static int _nextResourceHandle = unchecked((int)0x71000000);

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatchData
    {
        public ulong Skeletons;
        public ulong Bones;
        public ulong HierarchyLevels;
        public ulong HierarchyBoneIndices;
        public ulong InverseBindMatrices;
        public ulong ReferencePose;
        public ulong Clips;
        public ulong Samples;
        public ulong States;
        public ulong LocalPoses;
        public ulong GlobalMatrices;
        public ulong SkinMatrices;
        public ulong SkinWorkItems;
        public uint SkinWorkItemCount;
        public uint SkinPad;
        public float DeltaTime;
        public uint StateCount;
        public uint SkeletonTableCount;
        public uint ClipTableCount;
        public uint Pad;
    }

    private readonly RhiDevice _device;
    private readonly IEntityStore _world;
    private readonly AnimationFrameContext _animationContext;
    private readonly RhiShader _animationShader;
    private readonly RhiShader _skinShader;
    private readonly RhiPipeline _animationPipeline;
    private readonly RhiPipeline _skinPipeline;
    private readonly ResourceHandle _skeletonHandle = NextHandle();
    private readonly ResourceHandle _boneHandle = NextHandle();
    private readonly ResourceHandle _hierarchyLevelHandle = NextHandle();
    private readonly ResourceHandle _hierarchyIndexHandle = NextHandle();
    private readonly ResourceHandle _inverseBindHandle = NextHandle();
    private readonly ResourceHandle _referencePoseHandle = NextHandle();
    private readonly ResourceHandle _clipHandle = NextHandle();
    private readonly ResourceHandle _sampleHandle = NextHandle();
    private readonly ResourceHandle _stateHandle = NextHandle();
    private readonly ResourceHandle _localPoseHandle = NextHandle();
    private readonly ResourceHandle _globalMatrixHandle = NextHandle();
    private readonly ResourceHandle _skinMatrixHandle = NextHandle();
    private readonly ResourceHandle _skinWorkHandle = NextHandle();

    private RhiBuffer? _skeletonBuffer;
    private RhiBuffer? _boneBuffer;
    private RhiBuffer? _hierarchyLevelBuffer;
    private RhiBuffer? _hierarchyIndexBuffer;
    private RhiBuffer? _inverseBindBuffer;
    private RhiBuffer? _referencePoseBuffer;
    private RhiBuffer? _clipBuffer;
    private RhiBuffer? _sampleBuffer;
    private readonly RhiBuffer?[] _stateBuffers = new RhiBuffer?[PoseBufferCount];
    private readonly RhiBuffer?[] _localPoseBuffers = new RhiBuffer?[PoseBufferCount];
    private readonly RhiBuffer?[] _globalMatrixBuffers = new RhiBuffer?[PoseBufferCount];
    private readonly RhiBuffer?[] _skinMatrixBuffers = new RhiBuffer?[PoseBufferCount];
    private readonly RhiBuffer?[] _skinWorkBuffers = new RhiBuffer?[PoseBufferCount];
    private readonly List<RhiBuffer> _retiredBuffers = new();

    private int _assetFingerprint;
    private bool _assetsUploaded;
    private uint _skeletonCount;
    private uint _clipCount;
    private float _deltaTime = 1.0f / 60.0f;

    internal GpuAnimationPass(
        RhiDevice device,
        IEntityStore world,
        string contentRoot,
        Renderer renderer,
        AnimationFrameContext animationContext)
    {
        _device = device;
        _world = world;
        _animationContext = animationContext;
        Name = "GPU Animation Pose";
        Queue = RhiNative.QueueType.Graphics;

        string source = renderer.LoadShaderSource(
            "shaders/animation_gpu.slang",
            contentRoot);
        _animationShader = RhiShader.FromSource(
            device,
            source,
            "buildAnimationMain",
            RhiNative.ShaderStage.Compute,
            renderer.ActiveShaderIncludeDirs,
            renderer.ActiveShaderCliArgs);
        _skinShader = RhiShader.FromSource(
            device,
            source,
            "skinMain",
            RhiNative.ShaderStage.Compute,
            renderer.ActiveShaderIncludeDirs,
            renderer.ActiveShaderCliArgs);
        _animationPipeline = RhiPipeline.CreateCompute(device, _animationShader);
        _skinPipeline = RhiPipeline.CreateCompute(device, _skinShader);
        _animationPipeline.SetDebugName("GPU Animation Matrices", "Animation");
        _skinPipeline.SetDebugName("GPU Animation Skinning", "Animation");
    }

    public override void Setup(RenderGraphBuilder builder)
    {
        builder.ImportBuffer(_skeletonHandle);
        builder.ImportBuffer(_boneHandle);
        builder.ImportBuffer(_hierarchyLevelHandle);
        builder.ImportBuffer(_hierarchyIndexHandle);
        builder.ImportBuffer(_inverseBindHandle);
        builder.ImportBuffer(_referencePoseHandle);
        builder.ImportBuffer(_clipHandle);
        builder.ImportBuffer(_sampleHandle);
        builder.ImportBuffer(_stateHandle);
        builder.ImportBuffer(_localPoseHandle);
        builder.ImportBuffer(_globalMatrixHandle);
        builder.ImportBuffer(_skinMatrixHandle);
        builder.ImportBuffer(_skinWorkHandle);

        builder.Read(_skeletonHandle, ResourceState.ShaderRead);
        builder.Read(_boneHandle, ResourceState.ShaderRead);
        builder.Read(_hierarchyLevelHandle, ResourceState.ShaderRead);
        builder.Read(_hierarchyIndexHandle, ResourceState.ShaderRead);
        builder.Read(_inverseBindHandle, ResourceState.ShaderRead);
        builder.Read(_referencePoseHandle, ResourceState.ShaderRead);
        builder.Read(_clipHandle, ResourceState.ShaderRead);
        builder.Read(_sampleHandle, ResourceState.ShaderRead);
        builder.Read(_stateHandle, ResourceState.ShaderRead);
        builder.Write(_localPoseHandle, ResourceState.UnorderedAccess);
        builder.Write(_globalMatrixHandle, ResourceState.UnorderedAccess);
        builder.Write(_skinMatrixHandle, ResourceState.UnorderedAccess);
        builder.Read(_skinWorkHandle, ResourceState.ShaderRead);
    }

    public override unsafe void Execute(
        ICommandSink sink,
        RenderGraphContext context)
    {
        AdvanceAnimatorTimes();
        _animationContext.PrepareFrame(context.FrameNumber, _world);
        if (!TryBuildStates(
                out List<GpuAnimatorState> states,
                out List<ulong> entityIds,
                out uint totalBones))
            return;

        UploadImmutableAssets();
        int poseBufferIndex = (int)(context.FrameNumber % PoseBufferCount);
        int stateBufferIndex = poseBufferIndex;
        RhiBuffer stateBuffer = EnsureRuntimeBuffer(
            _stateBuffers,
            stateBufferIndex,
            (ulong)states.Count * (ulong)Marshal.SizeOf<GpuAnimatorState>());
        RhiBuffer localPoseBuffer = EnsureRuntimeBuffer(
            _localPoseBuffers,
            poseBufferIndex,
            (ulong)totalBones * (ulong)Marshal.SizeOf<LocalTransformGpu>());
        RhiBuffer globalMatrixBuffer = EnsureRuntimeBuffer(
            _globalMatrixBuffers,
            poseBufferIndex,
            (ulong)totalBones * 64ul);
        RhiBuffer skinMatrixBuffer = EnsureRuntimeBuffer(
            _skinMatrixBuffers,
            poseBufferIndex,
            (ulong)totalBones * 64ul);
        foreach (int stateIndex in Enumerable.Range(0, states.Count))
        {
            GpuAnimatorState state = states[stateIndex];
            _animationContext.SetSkinMatrices(
                entityIds[stateIndex],
                checked(skinMatrixBuffer.DeviceAddress +
                    (ulong)state.OutputMatrixOffset * 64ul));
        }
        SkinWorkItemGpu[] skinWorkItems = BuildSkinWorkItems();
        RhiBuffer skinWorkBuffer = EnsureRuntimeBuffer(
            _skinWorkBuffers,
            poseBufferIndex,
            checked((ulong)skinWorkItems.Length *
                (ulong)Marshal.SizeOf<SkinWorkItemGpu>()));
        if (skinWorkItems.Length > 0)
            skinWorkBuffer.Upload(skinWorkItems);
        stateBuffer.Upload(CollectionsMarshal.AsSpan(states));

        DispatchData dispatch = new()
        {
            Skeletons = _skeletonBuffer!.DeviceAddress,
            Bones = _boneBuffer!.DeviceAddress,
            HierarchyLevels = _hierarchyLevelBuffer!.DeviceAddress,
            HierarchyBoneIndices = _hierarchyIndexBuffer!.DeviceAddress,
            InverseBindMatrices = _inverseBindBuffer!.DeviceAddress,
            ReferencePose = _referencePoseBuffer!.DeviceAddress,
            Clips = _clipBuffer!.DeviceAddress,
            Samples = _sampleBuffer!.DeviceAddress,
            States = stateBuffer.DeviceAddress,
            LocalPoses = localPoseBuffer.DeviceAddress,
            GlobalMatrices = globalMatrixBuffer.DeviceAddress,
            SkinMatrices = skinMatrixBuffer.DeviceAddress,
            SkinWorkItems = skinWorkBuffer.DeviceAddress,
            SkinWorkItemCount = (uint)skinWorkItems.Length,
            DeltaTime = _deltaTime,
            StateCount = (uint)states.Count,
            SkeletonTableCount = _skeletonCount,
            ClipTableCount = _clipCount,
        };

        sink.BeginComputePass("GPU Animation Matrices");
        sink.BindPipeline(_animationPipeline);
        sink.UseBuffer(_skeletonBuffer, 1);
        sink.UseBuffer(_boneBuffer, 1);
        sink.UseBuffer(_hierarchyLevelBuffer, 1);
        sink.UseBuffer(_hierarchyIndexBuffer, 1);
        sink.UseBuffer(_inverseBindBuffer, 1);
        sink.UseBuffer(_referencePoseBuffer, 1);
        sink.UseBuffer(_clipBuffer, 1);
        sink.UseBuffer(_sampleBuffer, 1);
        sink.UseBuffer(stateBuffer, 1);
        sink.UseBuffer(localPoseBuffer, 2);
        sink.UseBuffer(globalMatrixBuffer, 2);
        sink.UseBuffer(skinMatrixBuffer, 2);
        sink.PushConstants(0, (uint)sizeof(DispatchData), (IntPtr)(&dispatch));
        sink.Dispatch(
            ((uint)states.Count + 63u) / 64u,
            1,
            1,
            64);
        sink.EndComputePass();
        if (skinWorkItems.Length == 0)
            return;

        sink.PipelineBarrier(
            new[]
            {
                new RhiNative.Barrier
                {
                    Resource = _skinMatrixHandle.Id,
                    StateBefore = RhiNative.ResourceState.UnorderedAccess,
                    StateAfter = RhiNative.ResourceState.UnorderedAccess,
                },
            });

        sink.BeginComputePass("GPU Animation Skinning");
        sink.BindPipeline(_skinPipeline);
        sink.UseBuffer(skinMatrixBuffer, 1);
        sink.UseBuffer(skinWorkBuffer, 1);
        foreach (AnimationFrameContext.SkinWorkItem workItem in _animationContext.WorkItems)
        {
            sink.UseBuffer(workItem.Mesh.SkinSourceBuffer!, 1);
            sink.UseBuffer(workItem.OutputBuffer, 2);
        }
        sink.PushConstants(0, (uint)sizeof(DispatchData), (IntPtr)(&dispatch));
        uint maximumVertexCount = 0;
        foreach (AnimationFrameContext.SkinWorkItem workItem in _animationContext.WorkItems)
            maximumVertexCount = Math.Max(maximumVertexCount, workItem.VertexCount);
        sink.Dispatch(
            (maximumVertexCount + 63u) / 64u,
            (uint)skinWorkItems.Length,
            1,
            64,
            1,
            1);
        sink.EndComputePass();
    }

    private bool TryBuildStates(
        out List<GpuAnimatorState> states,
        out List<ulong> entityIds,
        out uint totalBones)
    {
        states = new List<GpuAnimatorState>();
        entityIds = new List<ulong>();
        totalBones = 0;
        foreach (ulong entity in _world.Entities.OrderBy(entity => entity))
        {
            if (!_world.TryGet(entity, out AnimatorComponent animator) ||
                (animator.Flags & AnimatorComponent.ActiveFlag) == 0)
            {
                continue;
            }

            SkeletonAsset? skeleton =
                AnimationAssetRegistry.GetSkeleton(animator.SkeletonId);
            AnimationClipAsset? clip =
                AnimationAssetRegistry.GetClip(animator.BaseClipId);
            if (skeleton == null || clip == null ||
                clip.Metadata.SkeletonId != animator.SkeletonId)
            {
                continue;
            }

            try
            {
                skeleton.Validate();
                clip.Validate();
            }
            catch (InvalidDataException)
            {
                continue;
            }
            uint boneCount = checked((uint)skeleton.Bones.Length);
            entityIds.Add(entity);
            states.Add(new GpuAnimatorState
            {
                SkeletonId = animator.SkeletonId,
                EntityId = unchecked((uint)entity),
                BaseClipId = animator.BaseClipId,
                TargetClipId = 0,
                BaseTime = animator.Time,
                TargetTime = 0.0f,
                PlaybackRate = animator.PlaybackRate,
                TransitionTime = 0.0f,
                TransitionDuration = 0.0f,
                TransitionWeight = 0.0f,
                LayerOffset = 0,
                LayerCount = 0,
                Flags = animator.Flags,
                Generation = animator.Generation,
                OutputPoseOffset = totalBones,
                OutputMatrixOffset = totalBones,
                CurrentSkinnedVertexOffset = 0,
                PreviousSkinnedVertexOffset = 0,
            });
            totalBones = checked(totalBones + boneCount);
        }

        return states.Count > 0;
    }

    private void UploadImmutableAssets()
    {
        (IReadOnlyDictionary<uint, SkeletonAsset> skeletons,
            IReadOnlyDictionary<uint, AnimationClipAsset> clips) =
            AnimationAssetRegistry.Snapshot();
        int fingerprint = 17;
        foreach (var entry in skeletons.OrderBy(entry => entry.Key))
        {
            fingerprint = HashCode.Combine(
                fingerprint,
                entry.Key,
                entry.Value.Bones.Length,
                entry.Value.HierarchyLevels.Length,
                entry.Value.InverseBindMatrices.Length);
        }
        foreach (var entry in clips.OrderBy(entry => entry.Key))
        {
            fingerprint = HashCode.Combine(
                fingerprint,
                entry.Key,
                entry.Value.Samples.Length,
                entry.Value.Metadata.FrameCount,
                entry.Value.Metadata.BoneCount);
        }
        if (_assetsUploaded && fingerprint == _assetFingerprint)
            return;

        int maxSkeletonId = skeletons.Count == 0 ? 0 : skeletons.Keys.Max(id => checked((int)id));
        int maxClipId = clips.Count == 0 ? 0 : clips.Keys.Max(id => checked((int)id));
        var skeletonTable = new SkeletonAssetGpu[maxSkeletonId + 1];
        var clipTable = new AnimationClipGpu[maxClipId + 1];
        var bones = new List<BoneMetadataGpu>();
        var levels = new List<HierarchyLevelGpu>();
        var hierarchyIndices = new List<uint>();
        var inverseBinds = new List<Matrix4x4>();
        var referencePose = new List<LocalTransformGpu>();
        var samples = new List<LocalTransformGpu>();

        foreach (var entry in skeletons.OrderBy(entry => entry.Key))
        {
            SkeletonAsset skeleton = entry.Value;
            skeleton.Validate();
            uint boneOffset = checked((uint)bones.Count);
            uint levelOffset = checked((uint)levels.Count);
            uint hierarchyIndexOffset = checked((uint)hierarchyIndices.Count);
            bones.AddRange(skeleton.Bones);
            inverseBinds.AddRange(skeleton.InverseBindMatrices);
            referencePose.AddRange(skeleton.ReferencePose);
            foreach (HierarchyLevelGpu level in skeleton.HierarchyLevels)
            {
                levels.Add(new HierarchyLevelGpu
                {
                    BoneIndexOffset = checked(level.BoneIndexOffset + hierarchyIndexOffset),
                    BoneCount = level.BoneCount,
                });
            }
            hierarchyIndices.AddRange(skeleton.HierarchyBoneIndices);
            skeletonTable[checked((int)entry.Key)] = new SkeletonAssetGpu
            {
                BoneOffset = boneOffset,
                BoneCount = checked((uint)skeleton.Bones.Length),
                HierarchyLevelOffset = levelOffset,
                HierarchyLevelCount = checked((uint)skeleton.HierarchyLevels.Length),
                InverseBindOffset = checked((uint)(inverseBinds.Count - skeleton.InverseBindMatrices.Length)),
                ReferencePoseOffset = checked((uint)(referencePose.Count - skeleton.ReferencePose.Length)),
                RootBoneIndex = skeleton.RootBoneIndex,
                Flags = 0,
            };
        }

        foreach (var entry in clips.OrderBy(entry => entry.Key))
        {
            AnimationClipAsset clip = entry.Value;
            clip.Validate();
            AnimationClipGpu metadata = clip.Metadata;
            metadata.SampleOffset = checked((uint)samples.Count);
            samples.AddRange(clip.Samples);
            clipTable[checked((int)entry.Key)] = metadata;
        }

        _skeletonCount = checked((uint)skeletonTable.Length);
        _clipCount = checked((uint)clipTable.Length);
        _skeletonBuffer = UploadBuffer(_skeletonBuffer, skeletonTable, replaceExisting: true);
        _boneBuffer = UploadBuffer(_boneBuffer, bones.ToArray(), replaceExisting: true);
        _hierarchyLevelBuffer = UploadBuffer(_hierarchyLevelBuffer, levels.ToArray(), replaceExisting: true);
        _hierarchyIndexBuffer = UploadBuffer(_hierarchyIndexBuffer, hierarchyIndices.ToArray(), replaceExisting: true);
        _inverseBindBuffer = UploadBuffer(_inverseBindBuffer, inverseBinds.ToArray(), replaceExisting: true);
        _referencePoseBuffer = UploadBuffer(_referencePoseBuffer, referencePose.ToArray(), replaceExisting: true);
        _clipBuffer = UploadBuffer(_clipBuffer, clipTable, replaceExisting: true);
        _sampleBuffer = UploadBuffer(_sampleBuffer, samples.ToArray(), replaceExisting: true);
        _assetFingerprint = fingerprint;
        _assetsUploaded = true;
    }

    private RhiBuffer UploadBuffer<T>(
        RhiBuffer? existing,
        T[] values,
        bool replaceExisting = false)
        where T : unmanaged
    {
        ulong required = Math.Max(
            EmptyBufferBytes,
            checked((ulong)values.Length * (ulong)Marshal.SizeOf<T>()));
        if (replaceExisting || existing == null || existing.Size < required)
        {
            if (existing != null)
                _retiredBuffers.Add(existing);
            existing = RhiBuffer.Create(
                _device,
                required,
                RhiNative.BufferUsage.Storage);
        }
        if (values.Length > 0)
            existing.Upload(values);
        return existing;
    }

    private SkinWorkItemGpu[] BuildSkinWorkItems()
    {
        var result = new SkinWorkItemGpu[_animationContext.WorkItems.Count];
        for (int index = 0; index < result.Length; ++index)
        {
            AnimationFrameContext.SkinWorkItem workItem =
                _animationContext.WorkItems[index];
            result[index] = new SkinWorkItemGpu
            {
                SourceVertices = workItem.Mesh.SkinSourceBuffer!.DeviceAddress,
                OutputVertices = workItem.OutputAddress,
                SkinMatrices = workItem.SkinMatricesAddress,
                VertexCount = workItem.VertexCount,
                BoneCount = workItem.BoneCount,
                OutputOffset = workItem.OutputOffset,
            };
        }
        return result;
    }

    private RhiBuffer EnsureBuffer(RhiBuffer? existing, ulong required)
    {
        required = Math.Max(required, EmptyBufferBytes);
        if (existing != null && existing.Size >= required)
            return existing;
        if (existing != null)
            _retiredBuffers.Add(existing);
        return RhiBuffer.Create(
            _device,
            required,
            RhiNative.BufferUsage.Storage);
    }

    private RhiBuffer EnsureRuntimeBuffer(
        RhiBuffer?[] buffers,
        int index,
        ulong required)
    {
        required = Math.Max(required, EmptyBufferBytes);
        RhiBuffer? buffer = buffers[index];
        if (buffer == null || buffer.Size < required)
        {
            if (buffer != null)
                _retiredBuffers.Add(buffer);
            buffer = RhiBuffer.Create(
                _device,
                required,
                RhiNative.BufferUsage.Storage);
            buffers[index] = buffer;
        }
        return buffer;
    }

    private void AdvanceAnimatorTimes()
    {
        foreach (ulong entity in _world.Entities.OrderBy(entity => entity))
        {
            if (!_world.TryGet(entity, out AnimatorComponent animator) ||
                (animator.Flags & AnimatorComponent.ActiveFlag) == 0)
            {
                continue;
            }

            AnimationClipAsset? clip =
                AnimationAssetRegistry.GetClip(animator.BaseClipId);
            if (clip == null)
                continue;

            _world.Set(
                entity,
                AdvanceAnimatorTime(animator, clip, _deltaTime));
        }
    }

    internal static AnimatorComponent AdvanceAnimatorTime(
        AnimatorComponent animator,
        AnimationClipAsset clip,
        float deltaTime)
    {
        if ((animator.Flags & (1u << 3)) != 0 ||
            !float.IsFinite(deltaTime) ||
            !float.IsFinite(animator.PlaybackRate))
        {
            return animator;
        }

        float time = float.IsFinite(animator.Time)
            ? MathF.Max(animator.Time, 0.0f)
            : 0.0f;
        time += MathF.Max(deltaTime, 0.0f) * animator.PlaybackRate;
        float duration = clip.Metadata.Duration;
        bool looping = (animator.Flags & (1u << 1)) != 0 ||
            (clip.Metadata.Flags & (uint)AnimationClipFlags.Looping) != 0;
        if (duration > 0.0f && float.IsFinite(duration))
        {
            time = looping
                ? time % duration
                : MathF.Min(time, duration);
        }

        animator.Time = MathF.Max(time, 0.0f);
        return animator;
    }

    internal void SetDeltaTime(float deltaTime)
        => _deltaTime = float.IsFinite(deltaTime)
            ? Math.Clamp(deltaTime, 0.0f, 0.1f)
            : 1.0f / 60.0f;

    private static ResourceHandle NextHandle()
        => new(unchecked((uint)System.Threading.Interlocked.Increment(ref _nextResourceHandle)));

    public void Dispose()
    {
        _animationPipeline.Dispose();
        _skinPipeline.Dispose();
        _animationShader.Dispose();
        _skinShader.Dispose();
        _skeletonBuffer?.Dispose();
        _boneBuffer?.Dispose();
        _hierarchyLevelBuffer?.Dispose();
        _hierarchyIndexBuffer?.Dispose();
        _inverseBindBuffer?.Dispose();
        _referencePoseBuffer?.Dispose();
        _clipBuffer?.Dispose();
        _sampleBuffer?.Dispose();
        DisposeBuffers(_stateBuffers);
        DisposeBuffers(_localPoseBuffers);
        DisposeBuffers(_globalMatrixBuffers);
        DisposeBuffers(_skinMatrixBuffers);
        DisposeBuffers(_skinWorkBuffers);
        foreach (RhiBuffer buffer in _retiredBuffers)
            buffer.Dispose();
        _retiredBuffers.Clear();
    }

    private void DisposeBuffers(RhiBuffer?[] buffers)
    {
        for (int index = 0; index < buffers.Length; ++index)
        {
            buffers[index]?.Dispose();
            buffers[index] = null;
        }

    }
}
