# Visibility Buffer

## Purpose

The visibility buffer records the exact opaque geometry covering each pixel so
later compute shading can reconstruct surface attributes without a wide
material G-buffer. The clustered renderer uses visibility rasterization and
8×8 compute PBR as its default opaque path. Forward PBR remains available as
an explicit parity reference and for renderer instances that disable
visibility buffers, such as asset thumbnails.

## Public API Surface

| Symbol | Purpose |
| --- | --- |
| `RHI_FORMAT_RG32_UINT` / `TextureFormat.Rg32Uint` | Stores an exact raster-instance index and primitive index. |
| `RHI_FORMAT_RG16_UNORM` / `TextureFormat.Rg16Unorm` | Stores quantized barycentric X and Y. |
| `RhiPipeline.CreateGraphicsMrt` | Creates the two-color-target visibility raster pipeline. |
| `CommandRecorder.BeginRenderPass(ReadOnlySpan<RhiTexture>, ...)` | Begins a render pass with two to four color attachments. |
| `RenderGraphResources.VisibilityIdentifiersHandle` | Identifies the `RG32Uint` visibility texture. |
| `RenderGraphResources.VisibilityBarycentricsHandle` | Identifies the `RG16Unorm` barycentric texture. |
| `RenderGraphResources.VisibilityReconstructionHandle` | Identifies the `RGBA16Float` compute diagnostic texture. |
| `RenderGraphResources.VisibilityReferenceHandle` | Reserves an `RGBA16Float` Forward PBR reference identifier for focused validation plans. |
| `VisibilityBufferPass` | Rasterizes opaque indirect draws into both textures and scene depth. |
| `VisibilityReconstructionPass` | Reconstructs selected surface attributes for live validation modes. |
| `VisibilityReferencePass` | Implements focused Forward PBR validation for parity comparison. |
| `VisibilityShadingPass` | Runs shared PBR evaluation from visibility data with a tile-local deduplicated light list. |
| `VisibilityBufferDebugPass` | Presents normal compute shading or a selected visibility diagnostic before editor overlays. |
| `VisibilityPickingPass` | Enqueues one-pixel identifier and depth copies for non-blocking editor selection. |

## Storage Contract

`VisibilityIdentifiers.r` stores the raster instance supplied through
`SV_InstanceID`. The current indirect-draw ABI uses that value as a `PartData`
index. `PartData.instanceIdx` then resolves the higher-level `InstanceData`
record. This indirection is required because primitive indices restart for
every model part.

`VisibilityIdentifiers.g` stores `SV_PrimitiveID`, which is the triangle index
inside that part. `VisibilityBarycentrics.rg` stores the first two components
of `SV_Barycentrics` as 16-bit normalized values. Compute shading reconstructs
the third component as:

```slang
float2 barycentricXY = visibilityBarycentrics.Load(pixel).rg;
float3 barycentrics = max(float3(
    barycentricXY,
    1.0 - barycentricXY.x - barycentricXY.y), 0.0);
barycentrics /= max(
    barycentrics.x + barycentrics.y + barycentrics.z,
    1.0e-6);
```

Renormalization prevents a one-unit UNORM rounding difference at triangle
edges from making the weights sum slightly above or below one. Sixteen bits
per stored component bounds the component quantization error to about
`1 / 65535`, which is sufficient for material attribute reconstruction while
using half the storage of two 32-bit floats.

Scene depth distinguishes covered pixels from cleared background. Identifier
zero is therefore valid and is not reserved as an empty sentinel.

Editor picking reuses this storage contract. A click records one-pixel
identifier and depth blits into a shared-buffer ring. The graphics command
buffer signals a timeline fence after both copies, and a later update reads
mapped bytes only after that value completes. Each request retains a compact
part-to-entity snapshot from the recorded frame, so delayed results cannot
resolve against reordered scene data. Picking adds no geometry pass and no
CPU/GPU wait; normal latency is one or more frames depending on queue
completion.

## Usage Example

```csharp
RhiPipeline pipeline = RhiPipeline.CreateGraphicsMrt(
    device,
    vertexShader,
    fragmentShader,
    RhiNative.TextureFormat.Rg32Uint,
    RhiNative.TextureFormat.Rg16Unorm,
    enableDepth: true);
```

## Default Opaque Path

The clustered renderer inserts one visibility raster and one compute-shading
dispatch after GPU culling, clustered light assignment, and shadow updates.
They consume the same GPU-generated indirect commands, packed scene buffers,
mesh resources, shadow pages, and DDGI resources as Forward+ rendering. Normal
opaque frames do not execute the Forward PBR geometry pass. The visibility
result is presented before selection outlines, the editor grid, extension
post-passes, and ImGui so editor overlays remain composited on top.

Visibility shader source resolution checks the active project's shader folder
first, then the renderer's active plugin and engine include paths. Projects can
override the shader without copying the engine fallback into their own
`Content/shaders` directory.

Visibility resources allocate only for plans that set
`RendererPluginContext.EnableVisibilityBuffer`. Thumbnail and material-preview
renderers disable that flag and retain the compact Forward PBR path.

## Debug Visualization

Select **Visibility Buffer** in the viewport debug picker. The raster renderer
then composites the actual identifier, barycentric, and depth textures after
opaque rendering:

- The left half combines the `PartData` and primitive indices into a salted
  hash, then maps it to a vivid HSV colour. Every part/triangle pair—including
  `(0, 0)`—should have a stable non-grey colour while the camera moves.
- The right half displays reconstructed barycentric X, Y, and Z as RGB. Each
  triangle should show three clean colour corners with smooth gradients and no
  discontinuity away from shared edges.
- Uncovered depth renders near-black. Bright geometry outside the rasterized
  silhouette indicates stale identifiers or a depth/visibility mismatch.

Path tracing mirrors the same split visualization from ray-query hit data so
the shared selector remains valid in both renderer modes. Only the raster view
reads and validates the visibility-buffer textures themselves.

## Attribute Reconstruction

The visibility compute paths classify the material immediately after resolving
the part and before loading optional vertex attributes. Position and vertex
normal are mandatory. UVs are loaded only when a bound albedo, RMA, occlusion,
emissive, normal, or texture-mask channel needs them (or when a UV debug view
is explicitly selected). Tangent and handedness are loaded only for normal-map
and tangent-frame debug paths. Normal gradients are loaded only for curvature
mask evaluation. This keeps materials with only constant values from reading
UV and tangent fields from the interleaved vertex record.

The renderer computes a compact feature mask once while extracting each material
record. The mask covers every effective optional dependency: albedo, normal,
RMA, occlusion, emissive, texture masks, procedural layers, RMA-as-occlusion,
UVs, tangents, and curvature gradients. Materials with no texture slots or
active layers are classified as constant-material paths: they sample material
values and world-space geometric normal only, and skip UV, tangent, normal-map,
scalar-texture, texture-mask, procedural-layer, and curvature-gradient reads.
RMA containing AO is sampled once and its red channel is reused; a separate AO
sample is not issued.The VSM/material-qualifier pointers add only the current feedback-buffer addresses to `ScenePushData`; the scene part count also travels in the existing scalar push-data slot so stale visibility identifiers can be rejected before address loads. The material feature mask lives
in each `MaterialData` record and is recomputed during per-frame extraction so
editor and hot-reload changes cannot leave stale classification bits. The shader uses scalar vertex loaders
rather than materializing `Vertex` structs, allowing the compiler and backend
to issue only the selected field reads. Explicit neighbor-based UV and normal
gradients remain gated by the same classification; they do not rely on implicit
derivatives in compute.
The focused reconstruction shader reads each pixel's part and primitive
identifiers, decodes either 16-bit or 32-bit mesh indices, loads the required
attributes, renormalizes the stored barycentrics, and interpolates them.
Tangent-space normal maps are sampled with reconstructed UV gradients so mip
selection tracks the derivative-based sampling used by Forward PBR. The path
tracer uses the same classification helpers for its primary and ray-hit
material reconstruction. Magenta output means a stored primitive index exceeded
its part's index range.

The reconstruction pass is scheduled after visibility rasterization and before
compute shading. It executes only for reconstruction modes, while the shading
pass executes for lit, material, VSM, and other non-reconstruction modes. The
fullscreen presentation pass reads the same RGBA16F output for both families,
so the selected mode always has a producer before it is displayed. The Forward PBR reference pass is present in the graph for mode 20 but
records no geometry work for ordinary modes; it is only active when the parity
comparison is selected.

## Compute PBR Validation

The focused **Visibility PBR** diagnostic validates the compute-shading path.
The left half is conventional Forward+ PBR rendered into an `RGBA16Float`
reference target. The right half reconstructs the same world position,
geometric normal, tangent frame, texture coordinates, material, explicit
texture gradients, shadows, DDGI, and sky from visibility data. Differences
are amplified 8× and displayed as a dark-blue, blue, green, yellow, and red
heat map on the right; the yellow centre line separates it from the raster
reference. The comparison forces material AO to one on both paths so missing
or fully black occlusion assets cannot hide BRDF, metallic, shadow, or
reconstruction differences. Normal rendering continues to sample AO.

Each 8×8 compute group gathers the depth slices represented by its covered
pixels, reads the matching existing Forward+ cluster records, inserts their
light indices into a 1,024-entry group-shared open-addressed set, and compacts
the unique indices once for all 64 lanes. Surface lanes then run the shared
`ShadePbrSurface` evaluator over that tile list. Exact point and spotlight
attenuation remains per pixel, so this changes list-fetch and deduplication
cost without approximating coloured light transport or soft cone edges. The
existing clustered assignment remains the broad phase; visibility tiles are
a cooperative consumer of it rather than a second light-culling system.
The 1,024-entry set and compact list consume 8 KiB of threadgroup memory and
cover the worst-case union of all sixteen 64-light depth clusters without
drawing or shading a silently truncated light set.
Tiles with no covered geometry take a group-uniform early exit before clearing
the 1,024-entry light hash. They evaluate only the sky path, so sparse scenes
do not pay the light-list setup cost across the entire viewport.
The dispatch runs in a compute encoder on the graphics queue because it consumes
the same-frame visibility raster. Both visibility compute shaders use an 8×8×1
workgroup and the C# passes explicitly dispatch 8×8×1 threads per group; this
matches the 64-lane tile indexing (`lane = y * 8 + x`) and keeps the group
shared light-list synchronization scope identical to the screen tile. Dispatch
group counts are ceil-divided independently in X and Y, so partial edge groups
are safe through the shader's bounds checks. The earlier culling and cluster
assignment remain eligible for async compute ahead of graphics work.

The compute shader fixes its sampled texture registers at `t4`, `t5`, `t6`,
and `t7`, while its writable output uses `u0`. Optional material work is
feature-gated before vertex attribute reconstruction and texture evaluation;
ordinary lit constant materials still execute clustered lighting, VSM,
punctual shadows, DDGI, and constant emissive evaluation. Their tangent frame
uses a normal-derived fallback only when a normal-map or tangent diagnostic
requires it. The 8x8 tile remains one pipeline initially; mixed material
classes may diverge, so class-specific dispatches are deferred until GPU
profiling demonstrates that divergence costs more than the extra scheduling
and compaction work. The final sampled binding is a
typed `Texture2D<float>` view of the VSM depth atlas; depth textures do not use
the ordinary colour-texture bindless array in visibility compute shading.
Slang preserves the sampled Metal
texture indices but maps UAV registers into their own zero-based Metal texture
range. The RHI bindings therefore use 4–6 for identifiers, barycentrics, and
depth, 7 for VSM depth, and 0 for the writable output. Vulkan descriptor bindings mirror these
explicit values so both backends share the same shader contract.

The default renderer schedules the Forward PBR reference pass as a dormant
mode-gated validation stage. All ordinary lit and material debug modes present
compute shading directly without a second opaque draw; reconstruction modes use
the dedicated reconstruction dispatch.

The VSM atlas registration is persistent across scene changes. Scene resource
teardown clears the shared bindless heap and immediately re-registers the
persistent atlas so visibility compute shading never receives a stale texture
slot after loading or creating a scene.

The reconstruction and PBR parity modes remain internal diagnostics. The
viewport dropdown also exposes the VSM validation family: **VSM Shadow Map**
(virtual UV plus sampled depth), **VSM Depth**, **VSM Page Residency**, **VSM
Physical Page**, **VSM Page Requests**, **VSM Allocation Queue**, **VSM Raster
Coverage**, and **VSM Page Coordinates**. Magenta means that the receiver is
outside the valid light projection or that the required diagnostic resource is
not available; red/green/cyan/blue have mode-specific meanings described by
the labels and shader contract. **Material Qualifier** is available in the same
selector and renders a distinct categorical colour for every material type.
Constant materials are fixed green; every other feature-qualifier code is
hashed through a golden-ratio hue sequence so different material types receive
well-separated colours while identical materials stay identical. The visibility
tile path accumulates the golden-ratio-weighted qualifier codes of every
covered lane into one group-shared hash and displays the per-tile type colour.
Additive mixing preserves the type signature, so tiles of identical material
stay the same colour instead of cancelling; mixed tiles blend the weighted
signatures of their covered material types. The path tracer and Forward PBR fallbacks use
the same mapping. The qualifier is evaluated before optional UV/tangent
reconstruction. Its itemized Disney bits also distinguish metallic-only
materials, ordinary Disney diffuse, and subsurface diffuse. The visibility
shade evaluator uses those bits to skip the diffuse lobe entirely for fully
metallic materials, use an opaque Disney diffuse path without subsurface terms
for ordinary diffuse materials, and retain the full Disney diffuse/subsurface
path only when required. The opaque and subsurface implementations are separate
functions, so the non-subsurface path does not carry the Hanrahan–Krueger
reciprocal and interpolation work. This does not skip clustered
lighting, VSM, punctual shadows, DDGI, or constant emissive values.

The 8x8 visibility tile keeps light-list state in group-shared memory. Every
lane participates in initialization and barriers; covered lanes contribute
material workload metadata before the normal light-list path. Material feature
classification remains per pixel because a tile may contain mixed materials;
only the light list is shared across the tile.

## Performance Characteristics

Identifiers cost eight bytes per pixel and barycentrics cost four, for twelve
bytes per pixel excluding the already-owned depth texture. This is about 23.7
MiB at 1920×1080 and 94.9 MiB at 3840×2160. The visibility raster performs no
material or texture evaluation. Its `RGBA16Float` compute output adds eight
bytes per pixel. Focused parity plans can add an eight-byte-per-pixel
raster-reference target. Ordinary views issue one opaque visibility draw stream, one 8×8 shading
dispatch, and one fullscreen presentation; they do not redraw opaque geometry
through Forward PBR. The default plan does not record reconstruction or
Forward PBR reference passes.

The tile path deduplicates cluster lights now. A later optimization can apply
the same cooperative loading to DDGI probe indices and preload radiance and
visibility data into group-shared memory. Transparent geometry will continue
to use clustered Forward+ when authored transparency support is introduced.
Scenes with eight or fewer lights skip the full-screen cluster-assignment
dispatch and loop that short light array directly in covered visibility tiles.
The clustered broad phase turns on only above that threshold, removing its
fixed viewport-and-depth-slice cost from trivial and lightly lit scenes.
The direct path fills the compact tile list linearly and does not initialize
the 1,024-entry hash.

## Cross-References

- [PBR pipeline](../scene/pbr-pipeline.md)
- [Raster rendering plan](raster-rendering-plan.md)
- [RHI API](../rhi/api.md)
- [DDGI](ddgi.md)
- [Engine specification](../../engine-spec.md)
