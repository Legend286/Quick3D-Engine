# DDGI

## Purpose

`renderer.ddgi` supplies dynamic diffuse global illumination and a local
specular-reflection fallback to the clustered renderer. It has no authored
world-space volume. Three camera-relative
clipmaps prioritize updates into a persistent 262,144-slot GPU world cache.
Shading addresses that cache through exact world-cell keys, so the finest built
probes remain visible regardless of camera distance. Scrolling changes update
residency without recycling valid world-cell slots or hiding their irradiance
and visibility history.

## Public API

| Symbol | Purpose |
| --- | --- |
| `DDGIRendererPlugin` | Registers DDGI passes and exposes the active atlas through `IDDGIAtlasProvider`. |
| `DDGIProbeVolume` | Configures clipmap resolution, base cell size, level scale, and resident-slot budget. It contains no probe positions. |
| `DDGIAtlasResources` | Owns the persistent world atlas, world-cell keys and hash, active indirection, request, state, scheduling, and volume-metadata GPU resources. |
| `DDGIProbeResetPass` | Resets active/update counters and indirect draw arguments on the GPU. |
| `DDGIProbePlacementPass` | Scrolls the cache, performs geometry-aware density and clearance classification, and writes active/dormant state. |
| `DDGIProbeSchedulePass` | Selects the highest-priority active probes into a bounded GPU update queue. |
| `DDGIProbeUpdatePass` | Traces rays for queued probes and writes first-order SH irradiance, 4×4 octahedral visibility moments, and a slower four-level octahedral radiance cache. |
| `DDGIDebugPass` | Draws current camera and scene-bake requests from GPU-owned position and state buffers. |
| `IDDGIAtlasProvider` | Renderer-independent atlas, GPU-buffer, and plugin consumer-flag contract consumed by `PbrPass`. |
| `IDDGIAtlasProvider.GetSpecularBindlessSlot` | Returns the plugin-owned roughness-prefiltered radiance atlas slot. |
| `ISceneGpuDataProvider` | Shares current lights, canonical sky parameters, packed instances, mesh parts, materials and counts, scene bounds, and monotonic light/sky/geometry revisions with extension passes. |

## Usage

Enable the optional plugin in `.eeproj/addons.json`:

```json
{
  "version": 1,
  "enabled": [
    { "id": "renderer.ddgi", "version": "0.1.0" }
  ]
}
```

Select **DDGI Probes** in the viewport debug-view menu to display current
geometry-driven probe requests. **Probe status colours** is enabled by default
so the overlay remains visible before irradiance is ready; disable it to
reconstruct stored two-band SH irradiance on each octahedron face. Green means
ready and connected to the current sampling grid,
red means classified but never updated, orange means pending classification,
dirty, or converging after a radiance change, grey means valid cached data
outside current grid residency, and dark
means placement rejected the cell. Relocation and camera visibility do not
change state colour. Fine, medium, and far clipmaps use decreasing brightness
without changing diagnostic meaning.

Select **DDGI Indirect** to isolate raw received probe irradiance on scene
surfaces without albedo, metallic, or AO modulation. **Lighting Only** includes
direct and DDGI illumination with base colour divided out. Both are exclusive
surface modes and disable the **DDGI Probes** geometry overlay.

Both DDGI views are registered by `DDGIRendererPlugin`; neither name nor mode
exists in the engine's built-in debug enum. The PBR shader contains only a
`DDGI_PLUGIN` hook, while the plugin include owns indirect-debug behavior.

## GPU Pipeline

The extension plan executes before clustered shading:

Project switches and plugin toggles publish enabled shader features and include
directories before renderer extensions are attached. This guarantees that an
active DDGI plan and the clustered PBR pass agree on the `DDGI_PLUGIN` sampling
branch. The clustered plan never derives that feature from the atlas provider;
if editor activation is delayed, PBR remains on its valid host fallback until
the complete feature-and-include context is available.

1. **Reset** clears frame counters and writes the fixed-capacity indirect probe
   draw command with a one-thread compute dispatch.
2. **World-cache requests** map each active `(world cell, clipmap level)` key to
   a stable physical atlas slot and persistent GPU hash entry. Keys are never
   evicted. Scene bounds also
   produce a far-to-fine warm-up stream of 1 to 64 cells per frame. The default
   32-probe allowance produces 16 bake cells. Adding a large model such as
   Sponza changes the geometry revision and restarts this stream.
3. **Placement** uploads the active and warm-up requests, registers every key in
   the persistent lookup, rebuilds local update indirection, and classifies at
   most 128 camera requests plus 1 to 64
   bake requests with fourteen-direction inline TLAS queries. A request becomes
   geometry-active when those rays find nearby scene geometry and clearance.
   Empty camera-clipmap cells become sky-only probes; they capture sky once and
   remain dormant until the sky revision changes. Empty scene-bake cells remain
   inactive, so local-light edits cannot spend update budget on empty space.
   Placement also tests the candidate against each transformed mesh-part AABB;
   it does not use one coarse model or scene bound. A part overlap combined
   with a TLAS surface within twice the clearance radius forces relocation even
   when the closest hit alone would otherwise look marginal. Requiring both
   signals avoids treating a broad decorative-mesh bound as solid space.
   Relocation validates candidates on both sides of the closest surface,
   rejects a candidate below 75% of the half-metre clearance radius, and
   selects the nearest valid free-space result. It does not query triangle
   front-face state, so imported mesh winding cannot change validity and the
   generated Metal shader does not depend on a backend-specific ray-query
   facing intrinsic.
   A cached probe whose key and geometry revision are unchanged keeps its ready
   marker and atlas history when it re-enters a clipmap. If the TLAS is
   temporarily unavailable, placement remains pending and the initial-bake
   cursor pauses instead of committing an inactive probe at the current
   geometry revision.
4. **Schedule** scans the current request stream plus a rotating 8,192-slot
   window of the persistent atlas and writes 1 to 128 indices to
   `ProbeUpdateQueue` according to measured GPU cost. The persistent window
   eventually revisits built probes outside every current clipmap after a light
   change. Bake probes occupy at most
   half the current adaptive capacity, leaving
   capacity for visible camera probes. Priority then combines invalid history,
   geometry and lighting dirtiness, transformed mesh-part AABB overlap,
   visibility, residency, cascade level, and distance. Ready probes with no
   changed input are excluded rather than periodically refreshed.
5. **Update** dispatches only the queue capacity. Each probe traces 32 rays,
   evaluates the clustered renderer's canonical current-frame GPU light
   buffer at the triangle-interpolated world normal, samples the canonical
   Nishita sky on genuine scene misses, and admits incident sky irradiance at
   hits only when a second outward TLAS query is unobstructed. The trace range
   is at least 64 metres and expands to 105% of the current scene-bounds
   diagonal. It therefore clears the complete local scene before classifying a
   ray as sky instead of coupling misses to probe
   spacing. A temporarily unavailable TLAS retains existing probe history and
   defers the update; it never replaces enclosed probes with a frame of sky.
   Direct light evaluated at a ray hit also performs a TLAS visibility query
   toward its directional, point, or spotlight source, preventing walls from
   reflecting light whose source is geometrically occluded.
   Probe sky
   sampling excludes the tiny high-energy sun disc because the directional
   light is evaluated explicitly; this prevents a randomly aligned ray from
   injecting a bright SH outlier. The pass samples the hit material's
   base colour and metallic channel, stores four first-order SH irradiance
   coefficients, a 4×4 octahedral tile of directional distance moments, and a
   separate 4×4 octahedral radiance tile at four roughness levels,
   and records its update frame in `ProbeStates`. Every probe and update uses an
   independently scrambled, stratified 32-ray sphere to avoid coherent spatial
   patterns without altering RGB radiance. Stable updates retain 92% of atlas
   history. The first update after a lighting change retains 75% of the prior
   result, followed by three prioritized 50% convergence updates. The bounded
   staggered stream therefore reaches 90.625% of the new solution without a
   full-history single-frame flash. New, reclassified, and
   relocated probes replace invalid history immediately and then receive three
   prioritized running-average updates. The four batches provide 128 effective
   samples without desaturating or mixing colour channels. Before projection,
   a batch-relative luminance ceiling suppresses isolated high-energy ray hits
   by scaling all three RGB channels together. It therefore preserves hue, and
   consistently sampled coloured transport raises the batch mean and remains
   fully represented. New and relocated probes remain sample-hidden during
   those four initialization batches; shading fills their coverage from a
   coarser built level or the renderer fallback until the 128-sample result is
   ready.
   A successful replacement clears relocation state so subsequent updates can
   accumulate history. Directional-light direction,
   colour, intensity, angular radius, or shadow-state changes are part of that
   revision, so edited sun lighting enters the bounded retracing and convergence
   stream. Specular history has its own revision and timestamp. It refreshes at
   most once every eight frames during radiance edits and once every four frames
   while converging, while diffuse irradiance retains its faster cadence.
   Update rays also audit clearance. A ready probe that observes geometry within
   0.375 metres is marked dirty and pending, re-enters placement next frame,
   and loses ready status until its relocated position has been retraced.
6. **Clustered shading** converts the fragment position to eight exact world
   cells and queries `WorldProbeHash` from the finest level outward. It
   bilinearly samples visibility in the probe-to-surface octahedral direction,
   applies Chebyshev weighting, and adds diffuse indirect illumination for
   non-metallic energy. Complete fine support returns immediately. Missing fine
   corners lower confidence and are filled by the next built level, so clipmap
   movement cannot create a black border and already-built fine probes remain
   authoritative at distance. DDGI is sanitized and clamped. Each fragment
   reports its accumulated ready-probe confidence and fades out the renderer's
   constant ambient fallback only by that amount; uncovered geometry therefore
   retains the fallback while complete DDGI coverage removes it. Direct-light,
   shadowed, and emissive paths remain unchanged. If atlas setup is unavailable,
   confidence remains zero rather than making the frame black. A light or sky
   revision does not remove otherwise-ready probes from sampling: their prior
   radiance remains continuous until the bounded update stream blends in the
   new solution. This avoids missing-probe bands and isolated refreshed-probe
   colour patches while retaining full RGB transport. The shared PBR evaluator
   samples the radiance cache along the reflected view direction, interpolates
   its roughness levels, applies spatial probe visibility, and evaluates
   material Fresnel. This remains a fallback for a future primary screen-space
   or ray-traced reflection result.

Material AO modulates the non-DDGI ambient fallback but does not multiply DDGI.
Probe visibility moments already provide geometric indirect occlusion, and
applying an imported material AO map again can erase valid irradiance across a
level such as Sponza. Imported glTF metallic-roughness red channels are not
treated as AO, while textured metallic energy uses the sampled blue channel
rather than the uniform metallic factor.
7. **Debug** addresses the current request, position, and state buffers through
   GPU virtual addresses. Pending probes draw at their requested world position;
   active probes draw at their classified or relocated position. The editor
   reapplies the selected plugin debug view when a plugin reloads, so a selected
   probe overlay cannot silently revert to a disabled pass.

The GPU-owned buffers are:

| Buffer | Contents |
| --- | --- |
| `ProbePositions` | `float4` world position and active marker per resident slot. |
| `GridToProbeIndex` | Current local cell to resident-slot index, or `-1` for dormant cells. |
| `ProbeWorldKeys` | Exact signed world-cell coordinate and level for every persistent physical slot. |
| `WorldProbeHash` | Open-addressed slot lookup used by clustered shading independently of clipmap residency. |
| `ProbeStates` | Active, visible, dirty, pending, relocated, scene-bake, AABB-priority, sky-only, cascade, radiance-convergence count and initial-accumulation mode, last-update frame, geometry revision, and packed sampled light/sky revision. |
| `ProbeSpecularStates` | Independent reflection validity, last-update frame, packed radiance revision, and convergence state. |
| `ProbeRequests` | Triple-buffered active clipmap cells plus the bounded scene-bake batch, each mapped to a persistent physical slot. |
| `ProbeCounter` | Active, scheduled-update, camera-classification, and bake-classification counts. |
| `ProbeUpdateQueue` | Bounded list consumed by the update dispatch. |
| `ProbeDrawArgs` | Fixed-capacity non-indexed indirect draw arguments written by reset. |
| `VolumeState` | Per-level snapped centres, cell sizes, grid offsets, resolution, and frame. |

`IDDGIAtlasProvider.TryGetPersistentLookup` exposes `ProbeWorldKeys`,
`WorldProbeHash`, and its power-of-two capacity to renderer consumers.

## Unbounded Movement

The cache is not an authored scene volume and has no fixed world-space bounds.
Each clipmap still contains `11^3 = 1331` local cells: two-metre fine cells cover
22 metres, eight-metre medium cells cover 88 metres, and 32-metre far cells
cover 352 metres per axis. Those 3993 local entries point into 262,144 persistent
physical slots keyed by exact world cell and cascade. Moving away only changes
which probes receive camera-priority updates. It does not clear, reuse,
overwrite, or stop shading from the physical probe.

The cache deliberately has no eviction path. This guarantees that a valid probe
is never rebuilt because of camera movement. If all 262,144 keys are consumed,
existing GI remains intact and previously unseen cells stop allocating until
the plugin or scene session is reset. At the default sizes, a scene can retain
far more unique cells than the three active clipmaps while staying within a
small GPU-memory budget.

Changing to a different `SceneGraph` starts a new scene session and allocates a
fresh atlas, preventing identical world-cell coordinates in two levels from
sharing stale GI. Editing or importing geometry into the current scene keeps
the atlas and advances its geometry revision instead.

Scene geometry bounds seed a complete far-to-fine initial build. The stream
starts at sixteen cells, can contract to one when measured update capacity is
constrained, and scales to sixty-four when cost permits more work. Bake
production stays at or below half the update allowance, so every active bake
request can enter the same frame's priority queue rather than disappearing
uninitialized when the traversal advances. A geometry revision change restarts
the bounded traversal;
the revision fingerprint includes entity identity, transforms, bounds, part
geometry addresses, and index counts. When a many-part model such as Sponza
enters the renderer/TLAS, the change cannot alias a similarly sized existing
instance, and cached probes are reclassified and retraced against the new
scene. `IDDGIAtlasProvider.HasPendingWork` keeps low-power editor viewports
rendering until this scene traversal finishes instead of stopping after the
model-insertion burst. A light or sky revision also reserves two allocated-atlas
passes of refresh allowance and keeps the viewport rendering until that budget
is consumed. This lets the bounded 1–128 update queue and rotating persistent
scan propagate a directional or local-light edit after mouse or keyboard
interaction stops. Sky-only probes compare only the packed sky revision, so
moving a point or spot light does not wake empty-space work.
Radiance revisions never replace or dispose the atlas: they only mark relevant
probes for bounded retracing and blend their temporal history as each probe is
updated. Renderer-extension hot reload first retires every compiled DDGI pass,
then releases the atlas, so a frame cannot execute against disposed buffers.

## Performance

The renderer resets all GPU-work domains once before any render-graph pass, so
extension ordering and disabled shadow passes cannot preserve stale GI
admissions or erase current-frame diagnostics. The hard update ceiling is 128
probes. The 4 ms scheduler starts with a
32-probe estimate, feeds delayed GPU timings into its per-probe cost estimate,
and can grow or shrink the submitted count from 1 to 128. Scene-bake placement
uses half that allowance clamped from 1 to 64, so warm-up and camera-visible
work progress together. At 32 rays per probe the hard maximum is 4096 primary
rays. Hit rays add one sky-visibility query and visibility queries only for
lights whose unshadowed contribution is non-zero; the measured update cost
feeds the adaptive admission limit. A continuously changing light uses an
interactive tier capped at 24 probe updates and a rotating 2,048-probe
persistent scan. Eight stable radiance frames restore the adaptive 1–128
allowance and 8,192-probe scan for full convergence. A changing light cannot
immediately reselect a probe updated in the prior frame;
older lighting-dirty probes receive the refresh priority so the cache converges
instead of repeatedly updating only the closest slots. Placement dispatches one
thread per current request, but the fourteen classification rays run for at most 128
camera cells and 64 scene-bake cells. A probe close to or inside geometry runs
two additional fourteen-ray validations, one on each side of the nearest
surface. Geometry changes therefore stay bounded
instead of expanding one frame's work with scene complexity.
Ready probes are event-driven: geometry revisions reclassify them, light or sky
revisions retrace them, and convergence flags schedule the remaining temporal
steps. There is no periodic refresh, so an unchanged scene reaches an idle
state. Ready sky-only probes ignore local-light revisions; only a sky revision
makes them eligible again.
Initial and relocated probes consume four scheduler admissions over time, but
each admission remains a 32-ray dispatch and the adaptive per-frame probe cap is
unchanged. This trades warm-up latency for stable 128-sample history without a
single-frame ray-budget spike.
Placement and update share one frame-cached scene TLAS, so enabling DDGI does
not duplicate acceleration-structure extraction or builds. Scheduling uses a
128-thread compute group to scan current requests and one rotating 2,048-slot
interactive or 8,192-slot convergence window. Each lane retains its best two
candidates before a
bounded 256-entry shared-memory selection emits up to 128 unique probes. This
bounds scheduler work while covering the full 262,144-slot cache over 32
frames.

The update pass dispatches only the scheduler's admitted capacity. Delayed GPU
timestamps use a 16-frame submission history to feed the matching capacity and
measured pass cost back into the scheduler; no probe-counter readback or
render-thread wait is required. Timing samples train the per-probe estimate only
while scene-bake or radiance-refresh work is present, preventing idle dispatches
from making the next complex-scene burst look artificially cheap.

DDGI does not build or upload a CPU light tree. It shares the renderer's
triple-buffered `LightData` buffer, so moved ECS lights reach probe updates in
the same frame without a second scene extraction or synchronization upload.
The light fingerprint includes the complete position, direction, colour,
intensity, shape, and shadow fields. Directional-light edits therefore advance
`CurrentLightRevision` and receive lighting-dirty scheduling priority. The sky
has an independent fingerprint over its sun direction, angular radius,
intensity, turbidity, and ground albedo. Probe updates sample those exact sky
parameters without the explicit sun disc instead of adding a constant ambient
or synthetic gradient.

GPU virtual-address buffers are declared through `ICommandSink.UseBuffer` with
resource-usage flags only: `1` for read, `2` for write, and `3` for read/write.
The argument is not a shader binding slot. Passing other values can produce an
invalid backend usage mask and abort a Metal command buffer before presentation.

The irradiance atlas is `4096 × 256` RGBA16F, visibility is `4096 × 1024`
RGBA16F, and prefiltered reflection radiance is `4096 × 4096` RGBA16F.
Irradiance, visibility, radiance, positions, states, and world keys total about
184 MiB for 262,144 slots. The half-full 524,288-entry world hash adds 2 MiB.
Active indirection, triple-buffered requests, queue, counters, and clipmap
metadata add less than 512 KiB.

## Irradiance And Occlusion

Irradiance uses four RGB first-order SH coefficients packed into four
`RGBA16F` texels: 32 bytes per probe and 8 MiB for 262,144 slots. Second-order
SH would require nine texels and 18 MiB; third order would require sixteen
texels and 32 MiB. Diffuse GI
stays first-order because higher orders multiply per-pixel atlas reads while ray
tracing, directional visibility, and placement provide a better quality return.

Visibility stores mean hit distance and mean-square distance in a 4×4
octahedral `RGBA16F` tile per probe. The update pass projects all 32 traced
distances into each directional texel with a cosine-to-the-fourth lobe, broad
enough for the ray count to avoid under-sampled directional speckle, and
preserves stable temporal history.
Shading encodes the probe-to-surface vector, bilinearly reads the four nearest
moment texels, and applies Chebyshev weighting with a 2% minimum. The 16-texel
tile costs 128 bytes per probe and 32 MiB for 262,144 slots. It rejects light
transport crossing geometry in the sampled direction instead of applying one
non-directional visibility value to the entire probe. Bounded hit-to-light
shadow rays prevent occluded direct illumination from entering the SH result
without requiring higher-order irradiance SH.

## Reflection Fallback

Each physical probe owns 64 `RGBA16F` radiance texels: four 4×4 octahedral
tiles ordered from glossy to rough. When its independent cadence admits an
update, the same 32 traced RGB samples are projected through progressively
broader spherical lobes. This preserves coloured transport while suppressing
single-ray speckle as roughness increases. Bilinear octahedral filtering,
linear interpolation between adjacent roughness levels, temporal history, and
the existing probe-to-surface distance moments stabilize lookup without
desaturating radiance.

The cache is deliberately a fallback. A later screen-space or ray-traced
reflection may supply a primary result and confidence; the DDGI term can fill
missing rays without changing its storage or update policy. Direct analytic
lighting remains authoritative, and the probe sky excludes the explicit sun
disc so a narrow high-energy solar sample is neither duplicated nor allowed to
become a coloured reflection outlier.

## Cross-References

- [Shader modularity](shader-modularity.md)
- [Shader cache](shader-cache.md)
- [Render graph](render-graph.md)
- [RHI API](../rhi/api.md)
- [Engine specification](../../engine-spec.md)
