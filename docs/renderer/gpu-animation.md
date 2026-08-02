# GPU Animation Runtime

## Purpose

The Phase 1a runtime provides a GPU-owned animation clock, dense local-TRS clip
sampling, skeleton hierarchy evaluation, and skin-matrix generation. It is
integrated ahead of the existing renderer plan so raster visibility and the
path-tracing plugin retain one shared animation schedule.

Cooked meshes use `MSH1` for static geometry and `MSH2` for deforming
geometry. `MSH2` retains source position, normal, UV, tangent, four joint
indices, and four weights per vertex; the compute pass writes the same 48-byte
visibility `Vertex` stream used by static meshes. The source remains in
bind-pose space and the cooked part offset is applied after skinning. Static
geometry remains on the immutable path.

## Public API surface

- `Engine.RHI.AnimatorComponent` — CPU-owned animation intent containing
  skeleton/clip IDs, time, playback rate, active flags, and a generation.
- `Engine.Assets.SkeletonAsset` — immutable validated skeleton hierarchy,
  reference pose, and inverse-bind matrices.
- `Engine.Assets.AnimationClipAsset` — immutable frame-major dense local-TRS
  samples and clip metadata.
- `Engine.Assets.AnimationAssetRegistry` — process-local stable IDs and asset
  snapshots used to populate GPU tables.
- `Engine.Assets.GpuAnimatorState` — GPU state ABI mirrored by
  `Content/shaders/animation_gpu.slang`.

The internal `GpuAnimationPass` runs the GPU work and is inserted before the
canonical raster visibility sequence when an active animator exists while the
render plan is built. Path tracing reuses that raster base plan, so it receives
the same animation pass and does not create a second animation pipeline. The
static 48-byte vertex ABI remains unchanged; deforming output is written into a
frame-local stream with the same layout.

## Usage example

```csharp
SkeletonAsset skeleton = new()
{
    Bones = new[]
    {
        new BoneMetadataGpu { ParentIndex = -1, HierarchyDepth = 0 },
    },
    HierarchyLevels = new[]
    {
        new HierarchyLevelGpu { BoneIndexOffset = 0, BoneCount = 1 },
    },
    HierarchyBoneIndices = new[] { 0u },
    InverseBindMatrices = new[] { Matrix4x4.Identity },
    ReferencePose = new[] { LocalTransformGpu.Identity },
};
uint skeletonId = AnimationAssetRegistry.RegisterSkeleton(skeleton);
uint clipId = AnimationAssetRegistry.RegisterClip(new AnimationClipAsset
{
    Metadata = new AnimationClipGpu
    {
        FrameCount = 1,
        BoneCount = 1,
        SampleRate = 30,
        Duration = 1.0f,
        Flags = AnimationClipFlags.Looping,
        SkeletonId = skeletonId,
    },
    Samples = new[] { LocalTransformGpu.Identity },
});
world.Set(entity, AnimatorComponent.Create(skeletonId, clipId));
```

The animator must exist before the renderer compiles its scene plan. If an
animator is added after plan construction, call `Renderer.InvalidateRenderPlan`
on the render thread so the opt-in pass is inserted.

## Performance characteristics

- Immutable skeleton and clip data are uploaded only when the registry snapshot
  fingerprint changes.
- Runtime state, local poses, global matrices, and skin matrices use a
  three-buffer rotation to avoid overwriting in-flight GPU work.
- Pose work is dispatched with 64 threads per group, while skinning uses one
  additional dispatch row per deforming mesh stream. Parent-before-child
  hierarchy evaluation is deterministic; cost is proportional to active
  animators × bones plus deforming vertices.
- The pass uses graphics-queue scheduling initially, matching the specification's
  conservative queue policy. Async compute and indirect work lists remain later
  optimizations.

## Deferred boundaries

The current slice does not add root-motion extraction, blending, animation
events, bone masks, motion vectors, or BLAS updates. Those remain follow-up
work. Imported deforming meshes are now consumable by raster visibility and
path tracing through the shared frame-local output stream; static meshes retain
the unchanged `MSH1` path.

## Cross-references

- [GPU-driven animation specification](../../gpu-driven-animation-spec.md)
- [Raster rendering plan](raster-rendering-plan.md)
- [ECS components](../ecs/components.md)
- [Render graph](render-graph.md)
- [Engine architecture](../../engine-spec.md)
