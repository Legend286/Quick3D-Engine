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
| `VisibilityReconstructionPass` | Implements focused attribute validation outside the default plan. |
| `VisibilityReferencePass` | Implements focused Forward PBR validation outside the default plan. |
| `VisibilityShadingPass` | Runs shared PBR evaluation from visibility data with a tile-local deduplicated light list. |
| `VisibilityBufferDebugPass` | Presents normal compute shading or a selected visibility diagnostic before editor overlays. |

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

The focused reconstruction shader reads each 8×8 tile's part and primitive
identifiers, decodes either
16-bit or 32-bit mesh indices, loads the three vertices, renormalizes the stored
barycentrics, and interpolates the selected attributes. Tangent-space normal
maps are sampled with reconstructed UV gradients so mip selection tracks the
derivative-based sampling used by Forward PBR. Magenta output means a stored
primitive index exceeded its part's index range.

These transitional channels are not scheduled by the default renderer and do
not appear in the viewport dropdown. Their shader and pass remain available to
focused renderer tests while the raw visibility split is the supported live
diagnostic.

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
the same-frame visibility raster. The earlier culling and cluster assignment
remain eligible for async compute ahead of graphics work.

The default renderer does not schedule the Forward PBR reference or expose
this transitional mode. All ordinary lit and material debug modes present
compute shading directly without a second opaque draw.

The reconstruction and PBR parity modes remain internal diagnostics and are
not listed in the normal viewport dropdown. The public dropdown retains the
raw **Visibility Buffer** split view alongside the regular Lit and material
channels.

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
