# PBR Render Pipeline & GPU Culling

**Purpose**: Implements GPU-driven visibility-buffer PBR with clustered light
assignment and a Forward+ raster fallback for non-visibility renderers.
See [Scene Lights](lights.md) for authored point/spot/directional light
round-tripping through the editor and scene JSON.

## Render Passes
- `DirectionalShadowPass`: Updates budgeted directional cascade pages before
  forward shading.
- `RasterComputePass`: Issues compute shader-driven frustum culling, clustered
  light assignment, and indirect draw generation on async compute.
- `VisibilityBufferPass`: Records raster-instance and primitive indices in
  `RG32Uint`, barycentric X/Y in `RG16Unorm`, and opaque depth for the future
  compute-shading path.
- `VisibilityBufferDebugPass`: Presents visibility compute shading for normal
  views and replaces it with the selected visibility diagnostic when needed.
- `VisibilityReconstructionPass`: Implements focused position, normal, UV,
  material, instance, and tangent tests outside the default plan.
- `VisibilityReferencePass`: Implements a focused Forward PBR parity target
  outside the default plan.
- `PbrPass`: Owns shared culling, clustered-light, scene, and shading resources;
  it issues bindless Forward PBR geometry only when visibility is disabled.
- `GridPass`: Renders the editor wireframe infinite/fade grid. Runs concurrently with or after the PBR pass.
- `ImGuiPass`: Renders UI overlays.

Outline mask and composite passes record no render encoders while no entity is
selected. The stale mask does not matter because the composite is skipped in
the same state.

## Shaders
- `pbr.slang`: Forward renderer processing bindless geometry, textures, clustered light records, and Disney PBR material evaluation.
- `cull.slang`: Compute shader responsible for AABB-frustum intersection checks. Emits multi-draw indirect `RhiDrawCmd` arrays.
- `cluster_lights.slang`: Compute shader assigning scene lights into fixed-size per-cluster light-index lists.
- `visibility_buffer.slang`: Minimal opaque raster shader producing exact
  geometry identifiers and quantized barycentrics through two color targets.
- `visibility_buffer_debug.slang`: Reads visibility textures and depth for the
  fullscreen viewport diagnostic.
- `visibility_debug_common.slang`: Shares stable identifier hashing and split
  presentation with the path-tracing debug mirror.
- `visibility_reconstruct.slang`: Fetches triangle vertices and interpolates
  selected attributes from visibility-buffer data, including tangent-space
  normal maps sampled with reconstructed UV gradients.
- `visibility_reference.slang`: Produces the Forward PBR raster side of the
  attribute comparison.
- `grid.slang`: Line-list rendering of a standard 3D grid.

## Data Structures
Uses `PbrPushData` to send buffer addresses (materials, instances, models) globally via push constants. Eliminates per-draw CPU binding overhead.

Phase-one visibility uses the existing indirect draw ABI. Its stored raster
instance is a `PartData` index; `PartData.instanceIdx` resolves the scene
instance, and the stored primitive index selects one triangle within that part.
The third barycentric coordinate is reconstructed as `1 - x - y`.

## Clustered Forward+ Notes
- Cluster records are ordinary RHI storage buffers referenced through `ScenePushData`.
- The first implementation uses 32x32 screen tiles, 16 depth slices, and fixed 64-light slots per cluster.
- Directional lights are assigned to every cluster. Point lights use
  sphere-vs-cluster tests. Spot lights use a conservative sphere enclosing the
  complete finite outer cone, including its cap radius. The deliberate
  over-admission prevents a soft spotlight edge from being clipped at a
  cluster boundary; the per-pixel cone attenuation remains authoritative.
- A later RHI count-buffer/compaction pass can replace fixed slots without changing the PBR fragment interface.

The visibility-buffer validation path calls the same `ShadePbrSurface`
implementation from 8×8 compute groups. Each group unions and deduplicates the
existing Forward+ cluster lists represented by its covered depth slices in
group-shared memory. This reduces repeated cluster-list reads while preserving
the same per-pixel BRDF, coloured radiance, soft spotlight attenuation,
shadows, and DDGI evaluation as forward raster shading.

The main clustered renderer shades opaque geometry exclusively through the
visibility path. Forward PBR does not execute alongside it during normal
rendering. Asset thumbnail renderers deliberately disable visibility buffers
and retain Forward PBR to avoid allocating full viewport visibility targets.

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
- Camera or sun motion admits all dirty cascades together under a four-page,
  4 ms domain. Stable texel snapping still avoids updates while the effective
  projection is unchanged. Visibility compute shading refreshes its shadow
  parameters after the pass, keeping sampled matrices paired with same-frame
  depth pages.
- The page pool defaults to 1 GiB and clamps at 1.5 GiB. The four cascades
  consume 256 MiB; punctual-light tiers allocate additional 4096x4096 pages
  lazily.
- `RasterSceneGpuCache` extracts and uploads camera, light, instance, part, and
  material data once per frame for both the shadow and forward passes.
- Additional directional lights remain unshadowed until per-light atlas
  records are added.
- Directional, point, and spot receiver bias uses the interpolated geometric
  vertex normal. Tangent-space normal maps affect BRDF lighting only, so
  texture detail cannot vary the depth comparison offset from pixel to pixel.
- Thumbnail and hover-preview plans skip shadow allocation.

## Punctual Shadows
- Point and spot lights share an independent 6 ms GPU work budget. Completed
  pass timings update the scheduler's learned per-face cost. The baseline
  admits 24 faces. Four consecutive completed frames at or below 10.5 ms add
  six faces and 0.75 ms, up to 48 faces and 9 ms. Eight filtered frames at or
  above 14 ms remove one step. Frames between those thresholds hold the
  current setting. The frame input is a rolling 15-sample median, so delayed
  profiler results do not abruptly starve shadow refreshes.
- Each homogeneous GPU job contains at most 48 complete light faces, allowing
  the adaptive ceiling to remain one culling dispatch per light type: up to
  eight point-light cubemaps or 48 spots. Point and spot work never share a
  culling job, while unused face capacity can be filled by the other type.
- Each light keeps its committed sampling matrices and shadow origin paired
  with its cached static and movable tiles. Transform changes admit every face
  of that light atomically and publish the new state only after all required
  tile renders have been encoded. Deferred point lights select cached cubemap
  faces from the committed origin rather than the newer shading position, so
  they cannot expose mixed-frame seams while waiting.
- Deferred shadowed lights evaluate attenuation, spotlight cones, and BRDF
  direction from the committed position, direction, range, and shape paired
  with their atlas tiles. Colour and intensity remain live because they do not
  change shadow projection. Cluster assignment conservatively admits both the
  current and committed influence volumes, so a budget-delayed transform
  cannot lose lighting at a cluster boundary or compare a new light transform
  against an older shadow map.
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
  receive highest priority. Moved camera-relevant lights receive a bounded
  24-face freshness reserve when the learned allowance falls lower, preventing
  inaccurate historical timings from reducing interactive updates to one
  point light per frame.
  Valid lights become eligible only when their cadence
  deadline arrives. Transform changes precede scene-cache refreshes, then an
  overdue ratio and absolute-age bonus combine with strongly weighted visual
  priority.
  Nearby lights retain the fast lane under normal load, while sufficiently
  overdue distant lights still overtake them instead of starving. Batch
  construction never splits a point-light cubemap across frames.
- Deferred transform changes keep sampling the complete previously committed
  matrix, light origin, and tile set. All faces of an admitted light publish
  together, preventing mixed-frame cubemap seams and budget-driven flicker.
- Spot lights request one tile set. Point lights atomically request all six
  faces from the first tier that can contain the complete set; six faces
  therefore begin at the 4x4 tier rather than partially occupying 2x2.
- Newly allocated lights and resolution-migration tile sets wait one frame
  before their first write, ensuring every page is present in the graph's
  imported-resource bindings and barriers before it can be published.
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
