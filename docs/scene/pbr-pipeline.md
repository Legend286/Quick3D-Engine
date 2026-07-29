# PBR Render Pipeline & GPU Culling

**Purpose**: Implements the main forward+ physical-based rendering (PBR) pipeline alongside an early GPU-driven frustum culling mechanism.
See [Scene Lights](lights.md) for authored point/spot/directional light
round-tripping through the editor and scene JSON.

## Render Passes
- `DirectionalShadowPass`: Updates budgeted directional cascade pages before
  forward shading.
- `RasterComputePass`: Issues compute shader-driven frustum culling, clustered
  light assignment, and indirect draw generation on async compute.
- `PbrPass`: Issues bindless PBR geometry rendering.
- `GridPass`: Renders the editor wireframe infinite/fade grid. Runs concurrently with or after the PBR pass.
- `ImGuiPass`: Renders UI overlays.

## Shaders
- `pbr.slang`: Forward renderer processing bindless geometry, textures, clustered light records, and Disney PBR material evaluation.
- `cull.slang`: Compute shader responsible for AABB-frustum intersection checks. Emits multi-draw indirect `RhiDrawCmd` arrays.
- `cluster_lights.slang`: Compute shader assigning scene lights into fixed-size per-cluster light-index lists.
- `grid.slang`: Line-list rendering of a standard 3D grid.

## Data Structures
Uses `PbrPushData` to send buffer addresses (materials, instances, models) globally via push constants. Eliminates per-draw CPU binding overhead.

## Clustered Forward+ Notes
- Cluster records are ordinary RHI storage buffers referenced through `ScenePushData`.
- The first implementation uses 32x32 screen tiles, 16 depth slices, and fixed 64-light slots per cluster.
- Directional lights are assigned to every cluster. Point lights use sphere-vs-cluster tests; spot lights use finite-cone tests against cluster corners, edges, caps, and axis intervals.
- A later RHI count-buffer/compaction pass can replace fixed slots without changing the PBR fragment interface.

## Directional Shadows
- The first directional light with `CastShadows` enabled owns the raster shadow
  page pool.
- Four camera-centred horizontal clipmap cascades use radii of 5, 25, 125, and
  500 world units. Each cascade owns a separately clearable 4096x4096 depth
  page.
- Cascade selection uses radial XZ distance from the camera and ignores Y.
  Geometry directly below an airborne camera therefore retains the innermost
  available shadow quality. Adjacent radial bands blend at their boundaries.
- Each clipmap projection spans the scene's caster/receiver height and uses a
  square bounding-sphere footprint snapped in light space. The initial sphere
  centre and radius remain stable; an exceeded radius grows by at least 25
  percent and never shrinks.
- A GPU culling kernel tests transformed per-part AABBs against the selected
  cascade clip volume and writes the cascade's indirect draw commands. Cached
  invalidation signatures include only instances overlapping that cascade.
- Outer-cascade culling excludes geometry confined to an inner sampled band
  only when a conservative scene-height and sun-direction bound proves its
  shadow cannot reach the outer band. Transition overlap is three percent of
  the preceding radial band, with a 0.5-unit minimum.
- Cascade pages use stable bindless texture indices carried by
  `ScenePushData`.
- Raster PBR uses manual 3x3 PCF, edge clamping, cascade-scaled
  normal-dependent depth bias, and an overlap blend at cascade transitions.
- Dirty pages update under a one-page-per-frame budget. Cached matrices remain
  paired with cached depth pages until each update completes.
- The page pool defaults to 1 GiB and clamps at 1.5 GiB. The four cascades
  consume 256 MiB; punctual-light tiers allocate additional 4096x4096 pages
  lazily.
- `RasterSceneGpuCache` extracts and uploads camera, light, instance, part, and
  material data once per frame for both the shadow and forward passes.
- Additional directional lights remain unshadowed until per-light atlas
  records are added.
- Thumbnail and hover-preview plans skip shadow allocation.

## Punctual Shadows
- Point and spot lights share an independent 6 ms GPU work budget. Completed
  pass timings update the scheduler's learned per-tile cost.
- Each face keeps its committed sampling matrix paired with its cached static
  and movable tiles. A light transform admits both tile updates atomically and
  publishes the new matrix only after both renders complete.
- Atlas allocations remain stable for the lifetime of a light entity. Every
  allocated face republishes the same bindless page and tile indices each
  frame, including faces omitted from the current update schedule.
- Visible lights are prioritized by projected influence, intensity, range, and
  camera distance. Light influence volumes are tested against the camera
  frustum. Spotlights then use a camera-frustum versus shadow-frustum test.
  Visible point lights conservatively retain all six faces because corner-only
  frustum overlap tests can reject crossing cubemap-face volumes.
- Dirty punctual faces are scheduled globally. Invalid faces warm first, then
  the oldest committed face wins, with light priority breaking ties. A
  continuously moving light therefore cannot consume every frame's budget
  with its first cubemap faces and starve the remaining faces or other lights.
- Spot lights request one tile set. Point lights atomically request all six
  faces from the first tier that can contain the complete set; six faces
  therefore begin at the 4x4 tier rather than partially occupying 2x2.
- Newly allocated pages wait one frame before their first write, ensuring the
  page is present in the graph's imported-resource bindings and barriers.
- Every face has a cached static tile and a separately cached movable overlay.
  PBR samples both bindless depth tiles and combines their visibility.
- The GPU culler filters transformed part bounds by both face frustum and
  `static_shadow_caster` mobility before writing indirect draw commands.
- Admitted punctual tiles are uploaded as one GPU job array. A two-dimensional
  culling dispatch evaluates scene parts across every tile concurrently and
  writes disjoint indirect-command regions.
- Raster work is grouped by atlas page. Each touched page opens one depth
  encoder, clears all admitted tile rectangles, then renders all corresponding
  indirect-command regions while viewport/scissor clipping prevents tile
  bleed.
- Tile-local clears use a scissored depth-only draw with an `ALWAYS` compare
  pipeline, preserving every other tile on the shared page.
