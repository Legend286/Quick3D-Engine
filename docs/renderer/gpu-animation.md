# GPU Animation Runtime

## Purpose

The Phase 1a runtime provides a GPU-owned animation clock, dense local-TRS clip
sampling, skeleton hierarchy evaluation, and skin-matrix generation. It is
integrated ahead of the existing renderer plan so raster visibility and the
path-tracing plugin retain one shared animation schedule.

Cooked meshes use `MSH1` for static geometry and `MSH3` for new deforming
geometry. `MSH3` retains source position, normal, UV, tangent, four unsigned
32-bit joint indices, and four weights per vertex; legacy `MSH2` files with
float-encoded indices remain loadable through a conversion path. The compute
pass writes the same 48-byte visibility `Vertex` stream used by static meshes.
The source remains in bind-pose space and the cooked part offset is applied
after skinning. Static geometry remains on the immutable path. The shader
constructs local transforms in the column-vector convention used by
`mul(matrix, vector)`, with translation in the logical final column; the
`-matrix-layout-column-major` flag controls storage layout rather than changing
that multiplication convention.

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
- `Engine.Assets.GpuUInt4` — explicit unsigned joint-index ABI used by
  `SkinSourceVertexGpu`.
- `Engine.Assets.SkinWorkItemGpu` — per-stream GPU addresses, vertex count,
  and bone-count validation metadata.

The internal `GpuAnimationPass` is inserted before the canonical raster
visibility sequence in every raster render plan. It is dormant when the current
world has no valid animated deforming work, so shader and graph resources are
compiled once without delaying the first animated model. Path tracing reuses that
raster base plan, so it receives the same animation pass and does not create a
second animation pipeline. The static 48-byte vertex ABI remains unchanged;
deforming output is written into a frame-local stream with the same layout.
Model previews attach their animation sidecar before compiling the plan, which
allows a single animated model to begin playback immediately. Models dragged
into the viewport choose one imported clip and a random initial time when the
sidecar contains multiple clips; this gives repeated drops independent motion
until animator clip selection is exposed in the editor.

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

The animation pass is part of every raster scene plan, so adding an animator
does not require render-plan invalidation or shader recompilation. The animator
and its assets must be registered before the next frame executes; the pass then
activates automatically. Model-preview loading attaches the sidecar before the
preview plan is compiled.

## Performance characteristics

- Immutable skeleton and clip data are uploaded only when the registry snapshot
  fingerprint changes.
- CPU-owned animator time advances once per render frame. Runtime state,
  skin-work descriptors, local poses, global matrices, and skin matrices use a
  three-buffer rotation to avoid overwriting in-flight GPU work; each slot is
  uploaded from the same absolute CPU time so ring reuse cannot create separate
  clocks or current/previous-frame flicker.
  If a buffer must grow, the previous GPU allocation is retained until the pass
  is disposed rather than destroyed while an earlier command buffer may still
  reference it.
- CPU state construction retains the full packed `ulong` entity ID in a
  parallel mapping while the GPU state ABI keeps its 32-bit entity field. This
  prevents skin-matrix addresses from being assigned to the wrong work item when
  multiple animated entities are present.
- Matrix generation and vertex skinning are separate compute entry points and
  dispatches. The first dispatch builds complete skin matrices; the command
encoder ends before the second dispatch begins, with an explicit graph/RHI
barrier between them. On Metal, the encoder boundary is the visibility
mechanism; the barrier marker documents the hazard for backends that implement
explicit resource barriers. This prevents skinning threadgroups from observing
partially written matrices.
- Both dispatches use 64 threads per group. Skinning uses one Y dispatch row per
  deforming mesh stream. Parent-before-child hierarchy evaluation is
  deterministic; cost is proportional to active animators × bones plus
  deforming vertices.
- Joint indices remain integer data from the cook through shader access. Each
  skin work item carries its bone count; an out-of-range active influence falls
  back to the source bind-pose vertex rather than indexing arbitrary matrix
  memory. Zero-weight slots are ignored before index validation.
- The pass uses graphics-queue scheduling initially, matching the specification's
  conservative queue policy. Async compute and indirect work lists remain later
  optimizations. A dormant frame performs no animation or skinning dispatches.
  The animation shader treats animator state as read-only; CPU timing handles
  playback-rate, pause, and looping updates before upload. It constructs local
  transforms in the column-vector form consumed by the repository-wide
  `-matrix-layout-column-major`
  Slang configuration. TRS samples are uploaded as components. Inverse-bind
  matrices are decoded from glTF, conjugated for import scale, then transposed
  once by the cooker into CPU/System.Numerics ordering before serialization.
  The runtime uploads those values unchanged; the GPU matrix-layout convention
  then supplies the matching CPU-to-GPU interpretation and preserves bind-pose
  identity.

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
