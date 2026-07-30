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
  pass timings update the scheduler's learned per-face cost. The baseline
  admits 24 faces. Four consecutive completed frames at or below 10.5 ms add
  six faces and 0.75 ms, up to 48 faces and 9 ms. Eight filtered frames at or
  above 14 ms remove one step. Frames between those thresholds hold the
  current setting. The frame input is a rolling 15-sample median, so delayed
  profiler results do not abruptly starve shadow refreshes.
- Each GPU job contains at most 24 complete light faces. Adaptive admission can
  issue two jobs per frame when headroom permits, for up to eight point-light
  cubemaps or 48 spots. Point and spot work never share a culling or raster
  job, while unused face capacity can be filled by a job of the other type.
- Each light keeps its committed sampling matrices and shadow origin paired
  with its cached static and movable tiles. Transform changes admit every face
  of that light atomically and publish the new state only after all required
  tile renders have been encoded. Deferred point lights select cached cubemap
  faces from the committed origin rather than the newer shading position, so
  they cannot expose mixed-frame seams while waiting.
- Atlas allocations remain stable for the lifetime of a light entity. Every
  allocated face republishes the same bindless page and tile indices each
  frame, including faces omitted from the current update schedule.
- Visible lights are prioritized by projected influence size, distance from
  the camera to the light emitter, and a capped intensity contribution.
  Emitter distance selects cadence tiers of 1, 2, 3, 5, 8, or 10 frames.
  Projected radius can promote a visually large distant light, but cannot
  promote it beyond a three-frame cadence. Emitters within six metres update
  every frame while small lights beyond 100 metres update every ten.
- The same visual score selects stable atlas resolution tiers. Point-light
  faces use 1024, 512, 256, or 128 pixel tiles; spot lights use 2048, 1024,
  512, or 256 pixel tiles. Promotions require 12 stable frames and demotions
  require 90, preventing camera jitter from bouncing atlas allocations. A
  migration renders the complete light into its new tile set atomically and
  quarantines the old set for three frames before reuse.
- Light influence volumes are tested against the camera frustum. Spotlights
  then use a camera-frustum versus shadow-frustum test. Visible point lights
  conservatively retain all six faces because corner-only frustum overlap
  tests can reject crossing cubemap-face volumes.
- Dirty punctual lights are scheduled globally. Lights with any invalid face
  warm immediately. Valid lights become eligible only when their cadence
  deadline arrives. Transform changes precede scene-cache refreshes, then an
  overdue ratio and absolute-age bonus combine with weighted visual priority.
  Nearby lights retain the fast lane under normal load, while sufficiently
  overdue distant lights still overtake them instead of starving. Batch
  construction never splits a point-light cubemap across frames.
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
- Each per-type job slot owns persistent cull-job and indirect-command buffers.
  Filling a second homogeneous job cannot overwrite data still referenced by
  the first job's encoded GPU commands.
- Raster work is grouped by atlas page. Each touched page opens one depth
  encoder, clears all admitted tile rectangles, then renders all corresponding
  indirect-command regions while viewport/scissor clipping prevents tile
  bleed.
- Tile-local clears use a scissored depth-only draw with an `ALWAYS` compare
  pipeline, preserving every other tile on the shared page.
