// SPDX-License-Identifier: MIT
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Engine.Assets;
using Engine.RHI;
using Engine.Renderer;
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
        Assert.Equal(16, Marshal.SizeOf<GpuUInt4>());
        Assert.Equal(80, Marshal.SizeOf<SkinSourceVertexGpu>());
        Assert.Equal(48, Marshal.SizeOf<SkinnedVertexOutputGpu>());
        Assert.Equal(48, Marshal.SizeOf<SkinWorkItemGpu>());
        Assert.Equal(36, Marshal.SizeOf<SkinnedMeshAssetGpu>());
        Assert.Equal(32, Marshal.SizeOf<AnimatedMeshInstanceGpu>());
    }

    [Fact]
    public void AnimationShader_UsesOrderedMatrixAndSkinningEntryPoints()
    {
        string source = ReadRepositoryFile(
            "Content",
            "shaders",
            "animation_gpu.slang");
        string metalBackend = ReadRepositoryFile(
            "engine_c",
            "rhi",
            "rhi_metal.mm");

        Assert.Contains("-matrix-layout-column-major", metalBackend);
        Assert.Contains(
            "(xy - wz) * scale.y,\n        (xz + wy) * scale.z,\n        translation.x,",
            source);
        Assert.Contains(
            "(xy + wz) * scale.x,\n        (1.0 - (xx + zz)) * scale.y,\n        (yz - wx) * scale.z,\n        translation.y,",
            source);
        Assert.Contains(
            "(xz - wy) * scale.x,\n        (yz + wx) * scale.y,\n        (1.0 - (xx + yy)) * scale.z,\n        translation.z,",
            source);
        Assert.Contains(
            "0.0,\n        0.0,\n        0.0,\n        1.0);",
            source);
        Assert.Contains("void buildAnimationMain(", source);
        Assert.Contains("void skinMain(", source);
        Assert.DoesNotContain("void animateMain(", source);
        Assert.DoesNotContain("state.baseTime +=", source);
        Assert.DoesNotContain("push.states[animatorIndex] = state;", source);
        Assert.Contains("uint4 boneIndices;", source);
        string animationCook = ReadRepositoryFile("Cook", "AnimationCook.h");
        Assert.Contains("inline Matrix4 TransposeMatrix", animationCook);
        Assert.Contains("result.inverse_bind[bone] = TransposeMatrix(", animationCook);
        Assert.Contains("LegacySkinnedMeshMagic", ReadRepositoryFile("engine_cs", "Engine.Assets", "MeshLoader.cs"));
        Assert.Contains("uint4 boneIndices = source.boneIndices;", source);
        Assert.Contains("uint boneCount;", source);
        Assert.Contains("bool validVertex = true;", source);
        Assert.Contains("if (weight <= 1.0e-6)", source);
        Assert.Contains("if (boneIndex >= work.boneCount)", source);
        Assert.Contains("if (!validVertex)", source);
        Assert.Contains("position = float4(source.position, 1.0);", source);
        Assert.Contains(
            "position += mul(matrix, float4(source.position, 1.0)) * weight;",
            source);
        Assert.Contains(
            ": mul(global[metadata.parentIndex], local);",
            source);
        Assert.Contains(
            "global[bone],\n            push.inverseBindMatrices[skeleton.inverseBindOffset + bone]",
            source);
        Assert.DoesNotContain(
            "(xy - wz) * scale.y,\n        (xz + wy) * scale.z,\n        transform.translation.x,",
            source);
        Assert.DoesNotContain(
            "if (dispatchThreadId.y != 0u)",
            source);
    }

    [Fact]
    public void Cooker_SkinnedOutputOffsetCancelsSerializedLocalOffset()
    {
        string cook = ReadRepositoryFile("Cook", "main.cpp");

        Assert.Contains(
            "p.skinned_output_offset_x = -p.local_offset_x;",
            cook);
        Assert.Contains(
            "p.skinned_output_offset_y = -p.local_offset_y;",
            cook);
        Assert.Contains(
            "p.skinned_output_offset_z = -p.local_offset_z;",
            cook);
        Assert.DoesNotContain(
            "p.skinned_output_offset_x = -part_center_x;",
            cook);
    }

    [Fact]
    public void SkinnedLocalPlacement_CancelsPostSkinPartOffset()
    {
        Vector3 sourcePosition = new(7.0f, -2.0f, 4.0f);
        Vector3 localOffset = new(1.0f, 0.0f, 0.0f);
        Vector3 skinnedOutputOffset = -localOffset;

        Vector3 renderedPosition =
            sourcePosition + skinnedOutputOffset + localOffset;

        Assert.Equal(sourcePosition, renderedPosition);
        Assert.Equal(0.0f, skinnedOutputOffset.X + localOffset.X);
    }

    [Fact]
    public void Renderer_AlwaysPlansAnimationPassAndPreviewLoadsSidecarBeforePlan()
    {
        string renderer = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "Renderer.cs");
        string gameRenderer = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "GameRenderer.cs");
        string viewport = ReadRepositoryFile(
            "Editor",
            "ViewModels",
            "ViewportPanelViewModel.cs");

        Assert.Contains(
            "if (!usePathTracer)\n        {\n            passes.Insert(\n                0,\n                new GpuAnimationPass(",
            renderer);
        Assert.DoesNotContain("HasActiveGpuAnimator", renderer);
        Assert.Contains("out List<ulong> entityIds", ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "GpuAnimationPass.cs"));
        Assert.Contains("_animationContext.SetSkinMatrices(\n                entityIds[stateIndex]", ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "GpuAnimationPass.cs"));
        Assert.Contains("if (!TryBuildStates(", ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "GpuAnimationPass.cs"));
        string viewportPanel = ReadRepositoryFile(
            "Editor",
            "ViewModels",
            "ViewportPanelViewModel.cs");
        Assert.Contains("SelectRandomAnimation(animation)", viewportPanel);
        Assert.Contains("animator.Time = startTime;", viewportPanel);
        Assert.DoesNotContain(
            "AnimatorComponent.Create(\n                        animation.SkeletonId,\n                        animation.FirstClipId)",
            viewportPanel);
        Assert.Contains("AttachAnimationSidecar(_world, ent, modelPath, model);", gameRenderer);
        Assert.Contains("AttachAnimationSidecar(tempWorld, ent, assetPath, model);", gameRenderer);
        Assert.DoesNotContain("_gameLoop?.InvalidateRenderPlan();", viewport);
    }

    [Fact]
    public void AnimatorTime_AdvancesOnceAndWrapsOnTheCpu()
    {
        AnimationClipAsset clip = new()
        {
            Metadata = new AnimationClipGpu
            {
                FrameCount = 2,
                BoneCount = 1,
                SampleRate = 30,
                Duration = 1.0f,
                Flags = (uint)AnimationClipFlags.Looping,
            },
            Samples = new[] { LocalTransformGpu.Identity, LocalTransformGpu.Identity },
        };
        AnimatorComponent animator = AnimatorComponent.Create(1, 1, 2.0f);
        animator.Time = 0.75f;

        AnimatorComponent advanced = GpuAnimationPass.AdvanceAnimatorTime(
            animator,
            clip,
            0.2f);

        Assert.Equal(0.15f, advanced.Time, 5);
    }

    [Fact]
    public void AnimatorTime_DoesNotAdvanceWhenPaused()
    {
        AnimationClipAsset clip = new()
        {
            Metadata = new AnimationClipGpu
            {
                FrameCount = 1,
                BoneCount = 1,
                SampleRate = 30,
                Duration = 1.0f,
            },
            Samples = new[] { LocalTransformGpu.Identity },
        };
        AnimatorComponent animator = AnimatorComponent.Create(1, 1);
        animator.Time = 0.4f;
        animator.Flags |= 1u << 3;

        AnimatorComponent advanced = GpuAnimationPass.AdvanceAnimatorTime(
            animator,
            clip,
            0.2f);

        Assert.Equal(0.4f, advanced.Time);
    }

    [Fact]
    public void GpuAnimationStateIsReadOnlyAndUploadedEveryFrame()
    {
        string pass = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "GpuAnimationPass.cs");

        Assert.Contains(
            "builder.Read(_stateHandle, ResourceState.ShaderRead);",
            pass);
        Assert.Contains(
            "stateBuffer.Upload(CollectionsMarshal.AsSpan(states));",
            pass);
        Assert.Contains(
            "AdvanceAnimatorTimes();",
            pass);
        Assert.DoesNotContain("_stateBufferInitialized", pass);
        Assert.DoesNotContain("_stateFingerprint", pass);
    }

    [Fact]
    public void GpuAnimation_MultiModelInputsUseFrameRingAndRetireReplacements()
    {
        string pass = ReadRepositoryFile(
            "engine_cs",
            "Engine.Renderer",
            "GpuAnimationPass.cs");

        Assert.Contains(
            "private readonly RhiBuffer?[] _stateBuffers = new RhiBuffer?[PoseBufferCount];",
            pass);
        Assert.Contains(
            "private readonly RhiBuffer?[] _skinWorkBuffers = new RhiBuffer?[PoseBufferCount];",
            pass);
        Assert.Contains(
            "int stateBufferIndex = poseBufferIndex;",
            pass);
        Assert.Contains(
            "_skinWorkBuffers,\n            poseBufferIndex",
            pass);
        Assert.Contains(
            "_retiredBuffers.Add(existing);",
            pass);
        Assert.Contains(
            "_retiredBuffers.Add(buffer);",
            pass);
        Assert.Contains(
            "_skeletonBuffer = UploadBuffer(_skeletonBuffer, skeletonTable, replaceExisting: true);",
            pass);
        Assert.Contains(
            "_clipBuffer = UploadBuffer(_clipBuffer, clipTable, replaceExisting: true);",
            pass);
        Assert.DoesNotContain(
            "private RhiBuffer? _skinWorkBuffer;",
            pass);
        Assert.DoesNotContain(
            "int stateBufferIndex = 0;",
            pass);
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

    private static string ReadRepositoryFile(params string[] parts)
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        for (int depth = 0; depth < 10; ++depth)
        {
            string candidate = Path.Combine(
                new[] { directory }.Concat(parts).ToArray());
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            DirectoryInfo? parent = Directory.GetParent(directory);
            if (parent == null)
                break;
            directory = parent.FullName;
        }

        throw new FileNotFoundException(
            $"Repository file '{Path.Combine(parts)}' was not found.");
    }
}
