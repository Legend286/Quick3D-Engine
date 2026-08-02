# Visibility Buffer

## Purpose

The visibility buffer records the exact opaque geometry covering each pixel so
later compute shading can reconstruct surface attributes without a wide
material G-buffer. Phase one produces graph-visible identifiers,
barycentrics, and depth while the existing Forward+ PBR pass remains the final
shading path.

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
| `RenderGraphResources.VisibilityReferenceHandle` | Identifies the `RGBA16Float` Forward PBR raster-reference texture. |
| `VisibilityBufferPass` | Rasterizes opaque indirect draws into both textures and scene depth. |
| `VisibilityReconstructionPass` | Reconstructs selected surface attributes in 8×8 compute tiles. |
| `VisibilityReferencePass` | Rasterizes matching Forward PBR attributes for pixel-level validation. |
| `VisibilityShadingPass` | Runs shared PBR evaluation from visibility data with a tile-local deduplicated light list. |

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

## Phase One

The clustered renderer inserts one visibility raster before its first PBR
scene pass. It consumes the same GPU-generated indirect commands, packed scene
buffers, and mesh resources as Forward+ rendering. The existing PBR pass still
redraws and shades opaque geometry, making the additional raster cost explicit
and temporary while compute material reconstruction is not yet active.

Visibility shader source resolution checks the active project's shader folder
first, then the renderer's active plugin and engine include paths. Projects can
override the shader without copying the engine fallback into their own
`Content/shaders` directory.

Visibility resources allocate only for plans that set
`RendererPluginContext.EnableVisibilityBuffer`. Thumbnail and material-preview
renderers disable that flag.

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

The phase-two validation pass runs only while one of these debug views is
selected:

- **Reconstructed Position** matches the existing repeating world-position
  visualization.
- **Reconstructed Normal** displays the final world normal after tangent-space
  normal-map sampling.
- **Reconstructed UV** displays fractional texture coordinates.
- **Reconstructed Material ID** assigns one stable colour per material index.
- **Reconstructed Instance ID** combines the scene-instance index and entity
  identifier into a stable colour.
- **Reconstructed Tangent** displays the orthonormalized tangent after the
  tangent frame has been rebuilt around the final normal.

Each 8×8 compute tile reads the part and primitive identifiers, decodes either
16-bit or 32-bit mesh indices, loads the three vertices, renormalizes the stored
barycentrics, and interpolates the selected attributes. Tangent-space normal
maps are sampled with reconstructed UV gradients so mip selection tracks the
derivative-based sampling used by Forward PBR. Magenta output means a stored
primitive index exceeded its part's index range.

Reconstruction views display their selected compute-reconstructed attribute
directly across the viewport. They do not apply the PBR comparison's red error
overlay, so position, normal, UV, material, instance, and tangent channels
remain independently readable while diagnosing stored visibility data.

## Compute PBR Validation

Select **Visibility PBR** to validate the first complete compute-shading path.
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
The dispatch runs in a compute encoder on the graphics queue because it consumes
the same-frame visibility raster. The earlier culling and cluster assignment
remain eligible for async compute ahead of graphics work.

This view is deliberately a validation path. Forward PBR remains the default
opaque renderer until image differences are understood and measured.

## Performance Characteristics

Identifiers cost eight bytes per pixel and barycentrics cost four, for twelve
bytes per pixel excluding the already-owned depth texture. This is about 23.7
MiB at 1920×1080 and 94.9 MiB at 3840×2160. Phase one adds one depth-tested
opaque raster pass but performs no material or texture evaluation in that
pass. The `RGBA16Float` reconstruction and raster-reference diagnostics add
eight bytes per pixel each, bringing active comparison storage to twenty-eight
bytes per pixel excluding depth. Both diagnostic textures allocate lazily for
visibility validation modes. Reconstruction dispatch and attribute-reference
raster work run only for the six reconstruction modes. Compute PBR and its full
PBR reference run only for **Visibility PBR**. The identifier raster and its
textures are also inactive outside visibility debug views, so ordinary Forward+
rendering does not pay the temporary dual-raster validation cost. Selecting any
visibility debug view adds one fullscreen composite; other views record no
visibility raster, reconstruction, shading, reference, or composite work.

The staged phase-two tile path deduplicates cluster lights now. A later
optimization can apply the same cooperative loading to DDGI probe indices and
preload SH and visibility data into group-shared memory before opaque compute
shading becomes the default. Transparent geometry continues to use clustered
Forward+.

## Cross-References

- [PBR pipeline](../scene/pbr-pipeline.md)
- [Raster rendering plan](raster-rendering-plan.md)
- [RHI API](../rhi/api.md)
- [DDGI](ddgi.md)
- [Engine specification](../../engine-spec.md)
