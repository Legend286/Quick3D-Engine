# Raster Rendering Plan

## Purpose

This plan turns the current raster path into the engine's production Forward+
renderer while keeping the C RHI as the only graphics API boundary. Metal may be
the first backend, but every feature below is expressed in terms of buffers,
textures, pipelines, barriers, indirect dispatch/draw, heaps, and resource
state that also maps cleanly to Vulkan.

## Current Fixes

- Grow raster scene buffers before upload instead of relying on constructor-time
  capacities. This prevents large path-traced scenes from failing when switching
  back to raster mode.
- Size the indirect draw buffer from the number of renderable parts.
- Share culling shader scene layouts through `scene_data.slang` so compute
  culling and PBR drawing read identical `PartData` and `InstanceData` records.
- Make the raster cull pass zero indirect commands for frustum-culled
  instances.
- Share scene GPU extraction between raster and path tracing so both modes use
  the same dynamic buffer sizing and material layout.
- Add the first clustered Forward+ pass using RHI storage buffers and a compute
  shader light assignment step.
- Extract directional shadows into a graph-visible pass and share scene GPU
  uploads between shadow and forward rendering.
- Add four stable cascades backed by dedicated 4096x4096 depth pages.
- Persist cascade pages across frames and update at most one dirty page per
  frame under the shadow work budget.
- Use camera-centred XZ clipmap radii of 5, 25, 125, and 500 metres so vertical
  camera movement does not reduce ground shadow quality.
- Cull transformed part bounds against each selected light clip volume before
  emitting indirect shadow draws, and invalidate cached pages only for
  overlapping instances.
- Stabilize every cascade with a square region-bounding sphere and light-space
  texel snapping. Projection radii never shrink and grow only in coarse steps.
- Conservatively omit inner-band casters from outer pages when their maximum
  sun-projected displacement cannot reach the outer sampled band.
- Split raster culling and clustered-light assignment into a graph-visible
  compute pass that overlaps directional shadow rendering on async compute.

## Public API Surface

- `PbrPass`: Owns the raster Forward+ pass sequence and GPU-driven draw path.
- `DirectionalShadowPass`: Renders the directional shadow pages.
- `RasterSceneGpuCache`: Owns frame-keyed raster scene buffers shared by
  shadow and forward passes.
- `cull.slang`: Builds raster indirect draw commands from scene instance data.
- `scene_data.slang`: Shared GPU scene ABI for raster and path tracing.
- Future `rhi_cmd_dispatch_indirect`: Required for GPU-driven clustered and
  culling work without CPU readback.
- Future `rhi_cmd_draw_indexed_indirect_count`: Required for compacted GPU draw
  lists once the RHI exposes count-buffer draws.

## Phase 1: Stabilize Raster Scene Data

1. Replace duplicate extraction in `PbrPass` and `PathTracerPass` with a shared
   scene GPU data builder.
2. Add capacity telemetry for instances, parts, materials, lights, and indirect
   command buffers.
3. Add render-mode transition tests that load a large scene, render path tracing,
   switch to raster, and render at least one frame.
4. Add shader ABI checks for C# struct sizes against expected Slang layouts.

The immediate goal is correctness under large scenes, stable hot reload, and no
upload-size errors.

## Phase 2: GPU Frustum Culling

1. Convert the current one-command-per-part output into a compacted draw list.
2. Add a counter buffer and use atomic increments in `cull.slang`.
3. Extend the RHI with indirect count draws instead of drawing the full maximum
   part capacity.
4. Add per-meshlet or per-submesh bounds as the asset format grows.
5. Keep frustum extraction on CPU initially, then move camera planes to a
   persistent frame constants buffer used by all visibility passes.

The RHI abstraction should expose generic indirect draw count semantics:
buffer, count buffer, max draw count, stride. Metal maps this to
`drawPrimitives:indirectBuffer:indirectBufferOffset:` in a loop until native
count support is wrapped; Vulkan maps to `vkCmdDrawIndirectCount`.

## Phase 3: Clustered Forward Shading

1. Add a cluster grid builder compute pass using screen tile dimensions and
   fixed depth slices. First pass is implemented with 32x32 tiles, 16 slices,
   and fixed 64-light records per cluster.
2. Add a light assignment compute pass that tests point and spot light bounds
   against cluster bounds. Point lights now use sphere-vs-cluster tests. Spot
   lights now use finite-cone tests against cluster corners, edges, caps, and
   axis intervals.
3. Store light indices in GPU buffers using offset/count records per cluster.
   Implemented with fixed per-cluster slots.
4. Update `pbr.slang` to find the fragment cluster and evaluate only its light
   list. Implemented with fallback to all-lights shading when cluster records
   are unavailable.
5. Reserve directional lights in a small frame-level array because they affect
   all clusters. Current implementation assigns directional lights to all
   clusters.

All cluster data should be ordinary storage buffers. The RHI should only need
compute dispatch, resource barriers, storage buffer use, and eventually
dispatch-indirect for GPU-adaptive workloads.

## Phase 4: Dynamic Shadows

1. Shadow pages use the RHI shader-readable depth target and depth-only pass
   path. Directional shadows reserve four dedicated 4096x4096 pages.
2. Four directional cascades use horizontal radial selection, stable texel
   snapping, fixed square sphere footprints, 500-unit coverage,
   per-part GPU culling, 3x3 PCF, and transition blending.
3. Spot shadows use lazily allocated page tiers, cached static depth, and a
   separately refreshed movable overlay.
4. Point shadows atomically allocate six faces from one tier. Visible point
   lights retain every face and schedule invalid or oldest faces first,
   preventing moving-light update starvation. Spotlights retain conservative
   camera-frustum versus shadow-frustum rejection.
5. Add shadow receiver sampling to clustered light records so only shadowed
   lights pay shadow lookup cost.

Shadow pages are sampled through the existing bindless texture heap. Do not
expose Metal-specific argument-buffer concepts through C#.

## Phase 5: Volumetrics

1. Represent local fog volumes as spheres, finite cones, and boxes in a GPU
   volume buffer.
2. For each pixel or froxel, compute ray entry and exit points for the relevant
   finite shape before marching.
3. Implement ray-sphere intersection returning sorted `t0` and `t1`, clamped
   against camera near/far and scene depth.
4. Implement finite cone intersection by intersecting the infinite cone, then
   clipping candidates to cap planes and cone height; include the cap disk so
   rays entering through the base get correct `t0`.
5. March only within `[t0, t1]`, with steps distributed over the finite segment
   rather than over arbitrary camera depth.
6. For lit fog, sample clustered lights and shadow maps along the ray segment.
7. Add temporal-free quality modes first: deterministic jitter per frame can be
   optional, but base quality must not depend on temporal accumulation.

This avoids undersampling caused by marching empty space before and after a
light volume. The finite interval is the contract: every volume shader must
solve entry and exit analytically before stepping.

## Phase 6: Render Graph Integration

1. Promote the raster sequence to explicit graph passes:
   `DepthPrepass`, `GpuCull`, `ClusterBuild`, `LightCull`, `ShadowMap`,
   `ForwardOpaque`, `ForwardTransparent`, `Volumetrics`, and `Post`.
2. Track resource states in the C# render graph and lower barriers through the
   RHI barrier API.
3. Allocate transient cluster, shadow, and volumetric buffers through graph
   resources so memory can alias across non-overlapping passes.

## Usage Example

```csharp
var renderer = new Renderer(device, swapchain, world);
renderer.LoadScene("Content", "barrel");
renderer.UsePathTracer = false;
renderer.RenderFrame(backBuffer, width, height);
```

## Performance Characteristics

- GPU frustum culling reduces vertex work and indirect draw count before the
  main pass.
- Clustered Forward+ changes light cost from all-lights-per-fragment to
  visible-lights-per-cluster.
- The directional implementation owns four persistent 4096x4096 depth pages
  per raster renderer. Additional punctual-light pages allocate lazily under a
  1 GiB default budget and a 1.5 GiB hard ceiling. Thumbnail and hover-preview
  plans disable it.
- Scene GPU extraction is performed once per raster frame and reused by the
  directional shadow and forward passes.
- Volumetric raymarch cost scales with the finite volume segment length and
  configured step count, not full camera depth.

## Cross-References

- [engine-spec.md](../../engine-spec.md#5-renderer)
- [RHI API](../rhi/api.md)
- [Render Graph](render-graph.md)
- [PBR Pipeline](../scene/pbr-pipeline.md)
