# Skinned Instancing (shared animated vertex streams)

## Purpose

The compute skinning pipeline previously allocated a full skinned vertex stream
and issued a skinning dispatch item for every (entity, mesh) pair. With N
instances of the same animated model, that meant N× the vertex storage and N×
the skinning work even when every instance played the same clip in lockstep.
This document describes the shared-stream optimization that removes that waste.

## Core insight

The skinned vertex stream depends only on the **pose configuration**, not on
the entity:

- mesh (source vertices + weights)
- skeleton id
- base clip id
- playback rate
- clock time (`AnimatorComponent.Time`)
- animator flags (paused, looping, ...)
- part output offset (where in the output stream this part is skinned to)

The per-instance `modelMatrix` in `InstanceData` already differentiates
instances at draw time, so one shared stream can be rendered by any number of
instances whose pose configs are identical.

## Public API surface

`AnimationFrameContext` (internal, `engine_cs/Engine.Renderer/AnimationFrameContext.cs`)

- `PrepareFrame(long frameNumber, IEntityStore world)` — groups active animated
  entity/mesh pairs by `SkinStreamKey`, allocates **one** stream + one
  `SkinWorkItem` per group, and maps every group member to the shared
  buffer/device address.
- `TryGet(ulong entityId, ulong meshId, out RhiBuffer? buffer, out ulong deviceAddress)` —
  resolves the (shared) dynamic stream for an entity/mesh pair. Unchanged
  signature; callers (`SceneDataExtractor`) need no changes.
- `SetSkinMatrices(ulong entityId, ulong skinMatricesAddress)` — matches work
  items by entity id. Group members share a pose config, so their skin matrices
  are byte-identical; using the representative member's matrix address is
  correct. Unchanged semantics.

`SkinStreamKey` (private record struct): `MeshId, SkeletonId, BaseClipId,
PlaybackRate, BaseTime, AnimatorFlags, OutputOffset`.

## Usage example

No API change for consumers. Multiple instances with the same
`skeletonId`/`clipId`/`playbackRate`/`time` simply share one stream
automatically:

```
Instance A: spider, clip walk, rate 1.0, t=0.4s
Instance B: spider, clip walk, rate 1.0, t=0.4s  -> shares A's stream
Instance C: spider, clip walk, rate 0.5, t=0.4s  -> own stream (rate differs)
```

Instances split into separate streams the moment their pose configs diverge
(paused, scrubbed time, different clip/rate), so correctness is preserved
automatically — sharing is only ever applied when the output would be
byte-identical.

## Performance characteristics

- **Vertex storage:** N identical instances → 1 stream instead of N.
- **Skinning dispatch:** 1 skin work item per unique pose config instead of per
  instance.
- **Pose/matrix compute:** still one `GpuAnimatorState` per entity. The
  skin-matrix computation is tiny relative to vertex skinning, so deduplicating
  it is not worth the added state complexity.
- Sharing is frame-local: streams are re-grouped every frame in
  `PrepareFrame`, so pose divergence takes effect on the next frame.

## Cross-references

- `docs/ecs/components.md` — `AnimatorComponent` fields that form the key.
- `docs/renderer/ddgi.md` — the dynamic-vertex buffer ring shared with the
  renderer.
- `Content/shaders/animation_gpu.slang` — the compute shader that consumes
  `SkinWorkItemGpu`.
