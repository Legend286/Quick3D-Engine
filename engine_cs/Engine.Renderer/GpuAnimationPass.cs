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

/// <summary>Runs GPU animation-clock, dense-clip sampling, hierarchy, and skin-matrix work.</summary>
/// <remarks>
/// The pass is intentionally opt-in through <see cref="AnimatorComponent"/>.
/// Static scene geometry remains on the existing visibility path. Skinned mesh
/// source/output stream binding consumes the versioned MSH2 skinned-mesh
/// stream when an imported model provides bone influences.
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
    private readonly RhiShader _shader;
    private readonly RhiPipeline _pipeline;
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
    private readonly RhiBuffer?[] _stateBuffers = new RhiBuffer?[1];
    private readonly bool[] _stateBufferInitialized = new bool[1];
    private readonly RhiBuffer?[] _localPoseBuffers = new RhiBuffer?[PoseBufferCount];
    private readonly RhiBuffer?[] _globalMatrixBuffers = new RhiBuffer?[PoseBufferCount];
    private readonly RhiBuffer?[] _skinMatrixBuffers = new RhiBuffer?[PoseBufferCount];
    private RhiBuffer? _skinWorkBuffer;

    private int _assetFingerprint;
    private int _stateFingerprint;
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
        _shader = RhiShader.FromSource(
            device,
            source,
            "animateMain",
            RhiNative.ShaderStage.Compute,
            renderer.ActiveShaderIncludeDirs,
            renderer.ActiveShaderCliArgs);
        _pipeline = RhiPipeline.CreateCompute(device, _shader);
        _pipeline.SetDebugName("GPU Animation Pose", "Animation");
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
        builder.ReadWrite(_stateHandle, ResourceState.UnorderedAccess);
        builder.Write(_localPoseHandle, ResourceState.UnorderedAccess);
        builder.Write(_globalMatrixHandle, ResourceState.UnorderedAccess);
        builder.Write(_skinMatrixHandle, ResourceState.UnorderedAccess);
        builder.Write(_skinWorkHandle, ResourceState.UnorderedAccess);
    }

    public override unsafe void Execute(
        ICommandSink sink,
        RenderGraphContext context)
    {
        _animationContext.PrepareFrame(context.FrameNumber, _world);
        if (!TryBuildStates(out List<GpuAnimatorState> states, out uint totalBones))
            return;

        UploadImmutableAssets();
        int stateBufferIndex = 0;
        int poseBufferIndex = (int)(context.FrameNumber % PoseBufferCount);
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
                state.EntityId,
                checked(skinMatrixBuffer.DeviceAddress +
                    (ulong)state.OutputMatrixOffset * 64ul));
        }
        SkinWorkItemGpu[] skinWorkItems = BuildSkinWorkItems();
        if (skinWorkItems.Length > 0)
        {
            _skinWorkBuffer = EnsureBuffer(
                _skinWorkBuffer,
                checked((ulong)skinWorkItems.Length * (ulong)Marshal.SizeOf<SkinWorkItemGpu>()));
            _skinWorkBuffer.Upload(skinWorkItems);
        }
        int stateFingerprint = ComputeStateFingerprint(states);
        if (stateFingerprint != _stateFingerprint)
        {
            _stateFingerprint = stateFingerprint;
            Array.Fill(_stateBufferInitialized, false);
        }
        if (!_stateBufferInitialized[stateBufferIndex])
        {
            stateBuffer.Upload(CollectionsMarshal.AsSpan(states));
            _stateBufferInitialized[stateBufferIndex] = true;
        }

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
            SkinWorkItems = _skinWorkBuffer?.DeviceAddress ?? 0,
            SkinWorkItemCount = (uint)skinWorkItems.Length,
            DeltaTime = _deltaTime,
            StateCount = (uint)states.Count,
            SkeletonTableCount = _skeletonCount,
            ClipTableCount = _clipCount,
        };

        sink.BeginComputePass(Name);
        sink.BindPipeline(_pipeline);
        sink.UseBuffer(_skeletonBuffer, 1);
        sink.UseBuffer(_boneBuffer, 1);
        sink.UseBuffer(_hierarchyLevelBuffer, 1);
        sink.UseBuffer(_hierarchyIndexBuffer, 1);
        sink.UseBuffer(_inverseBindBuffer, 1);
        sink.UseBuffer(_referencePoseBuffer, 1);
        sink.UseBuffer(_clipBuffer, 1);
        sink.UseBuffer(_sampleBuffer, 1);
        sink.UseBuffer(stateBuffer, 2);
        sink.UseBuffer(localPoseBuffer, 2);
        sink.UseBuffer(globalMatrixBuffer, 2);
        sink.UseBuffer(skinMatrixBuffer, 2);
        if (_skinWorkBuffer != null)
            sink.UseBuffer(_skinWorkBuffer, 2);
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
            Math.Max(
                ((uint)states.Count + 63u) / 64u,
                (maximumVertexCount + 63u) / 64u),
            skinWorkItems.Length == 0
                ? 1u
                : checked((uint)skinWorkItems.Length + 1u),
            1,
            64);
        sink.EndComputePass();
    }

    private bool TryBuildStates(
        out List<GpuAnimatorState> states,
        out uint totalBones)
    {
        states = new List<GpuAnimatorState>();
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
        _skeletonBuffer = UploadBuffer(_skeletonBuffer, skeletonTable);
        _boneBuffer = UploadBuffer(_boneBuffer, bones.ToArray());
        _hierarchyLevelBuffer = UploadBuffer(_hierarchyLevelBuffer, levels.ToArray());
        _hierarchyIndexBuffer = UploadBuffer(_hierarchyIndexBuffer, hierarchyIndices.ToArray());
        _inverseBindBuffer = UploadBuffer(_inverseBindBuffer, inverseBinds.ToArray());
        _referencePoseBuffer = UploadBuffer(_referencePoseBuffer, referencePose.ToArray());
        _clipBuffer = UploadBuffer(_clipBuffer, clipTable);
        _sampleBuffer = UploadBuffer(_sampleBuffer, samples.ToArray());
        _assetFingerprint = fingerprint;
        _assetsUploaded = true;
    }

    private static int ComputeStateFingerprint(
        IReadOnlyList<GpuAnimatorState> states)
    {
        int fingerprint = 17;
        foreach (GpuAnimatorState state in states)
        {
            fingerprint = HashCode.Combine(
                fingerprint,
                state.EntityId,
                state.SkeletonId,
                state.BaseClipId,
                state.BaseTime,
                state.PlaybackRate,
                state.Flags,
                state.Generation);
            fingerprint = HashCode.Combine(
                fingerprint,
                state.OutputPoseOffset,
                state.OutputMatrixOffset);
        }
        return fingerprint;
    }

    private RhiBuffer UploadBuffer<T>(RhiBuffer? existing, T[] values)
        where T : unmanaged
    {
        ulong required = Math.Max(
            EmptyBufferBytes,
            checked((ulong)values.Length * (ulong)Marshal.SizeOf<T>()));
        if (existing == null || existing.Size < required)
        {
            existing?.Dispose();
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
        existing?.Dispose();
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
            buffer?.Dispose();
            buffer = RhiBuffer.Create(
                _device,
                required,
                RhiNative.BufferUsage.Storage);
            buffers[index] = buffer;
        }
        return buffer;
    }

    internal void SetDeltaTime(float deltaTime)
        => _deltaTime = float.IsFinite(deltaTime)
            ? Math.Clamp(deltaTime, 0.0f, 0.1f)
            : 1.0f / 60.0f;

    private static ResourceHandle NextHandle()
        => new(unchecked((uint)System.Threading.Interlocked.Increment(ref _nextResourceHandle)));

    public void Dispose()
    {
        _pipeline.Dispose();
        _shader.Dispose();
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
        _skinWorkBuffer?.Dispose();
        _skinWorkBuffer = null;
    }

    private void DisposeBuffers(RhiBuffer?[] buffers)
    {
        for (int index = 0; index < buffers.Length; ++index)
        {
            buffers[index]?.Dispose();
            buffers[index] = null;
        }

        if (ReferenceEquals(buffers, _stateBuffers))
            Array.Clear(_stateBufferInitialized);
    }
}
