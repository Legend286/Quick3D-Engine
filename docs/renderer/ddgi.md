# DDGI — Dynamic Diffuse Global Illumination

## Purpose

`renderer.ddgi` is the engine's indirect-diffuse lighting plugin. When
enabled, host PBR shading (`Content/shaders/pbr.slang`) replaces its
fallback hemi-tint with an SH2 reconstruction drawn from a probe
atlas covering the active scene volume. The plugin is **modular** —
toggle it on and the override engages; toggle it off and the host
passes compile back to the bundled stub. No source fork required.

The plugin contributes four things:

1. A **probe volume** that defines the spatial region over which
   probes are scattered (`DDGIProbeVolume`).
2. A **GPU-owned update budget** that refreshes the dense active probe
   prefix each frame from the placement counter. `DDGIProbePriority`
   remains available for offline tooling but is not used by the runtime.
3. A **light tree** over punctual lights so probe rays can
   efficiently traverse only relevant emitters
   (`DDGILightBVH`).
4. A **2 ms per-frame budget gate** on the
   `GpuWorkDomain.Gi` admit slot so probe updates can never
   dominate the frame.

The runtime is GPU-owned after the volume bounds are created. A graphics-queue
placement kernel rebuilds the sparse position list, indirection table, active
counter, and indirect debug draw arguments every frame. A following update
kernel launches the fixed probe budget and culls inactive groups from that
counter, then writes SH and visibility atlas data. The CPU submits scene AABBs
and light snapshots, but never owns probe positions, active count, relocation,
or update selection.

## Public API surface

| Symbol | Kind | Role |
| --- | --- | --- |
| `GpuWorkDomain.Gi` | enum value | 2.0 ms ceiling per frame; per-probe cost tracked via `RecordCompletedWork`. |
| `DDGIProbeVolume` | `Engine.Renderer.DDGI` | CPU-owned volume bounds and grid metadata; GPU placement owns positions and active count. |
| `DDGIProbePriority` | same | Legacy CPU priority helper retained for offline tooling; not used by the GPU-owned runtime. |
| `DDGILightBVH` | same | CPU binary-volume hierarchy over punctual lights with `GetBoundingSphere(orderedLightIndex)` for shader ray-box tests. |
| `DDGIRendererPlugin` | `Engine.DDGI`, plugin entry | Implements `IRendererPlanPlugin`; wires GPU placement, GPU-counted updates, light uploads, PBR sampling, and indirect debug drawing. |
| `ddgi_sampling.slang` | shader | `SampleIndirectDiffuse(worldPos, worldNormal)` — entered via `#ifdef DDGI_PLUGIN` in `pbr.slang`. |

## Usage example

Enable the plugin from `Modules.json`:

```json
{
  "plugins": {
    "enable": ["renderer.ddgi"]
  }
}
```

Or via the editor's **Tools → Plugins** dialog. The plugin's
`BuildPlan(context)` runs once per scene compile and:

1. Uploads `context.Scene.Lights` (a `SceneGraph.Lights` list of
   `LightNode`s with type/position/range/intensity/etc.) into the GPU
   light snapshot and light tree.
2. Schedules GPU placement before the host renderer. Placement writes
   dense probe positions, grid indirection, the active counter, and
   indirect debug draw arguments.
3. Dispatches the fixed GPU probe budget. Each update group reads the
   placement counter and exits for inactive slots; there is no CPU
   probe list, readback, or per-probe scheduler.
4. Lets PBR sample the GPU-written SH/visibility atlas and lets the
   debug pass consume the GPU indirect draw arguments.

Host shader engagement is automatic: the manifest's
`shader_features: ["DDGI_PLUGIN"]` flows through
`EditorShaderBridge.ActiveShaderContextChanged` and reaches
`pbr.slang` as `-D DDGI_PLUGIN=1`. The `#include "ddgi_sampling.slang"`
inside `pbr.slang` resolves through the
`ShaderIncludeResolver`-merged include dirs.

> **Phase-2 limit**: `BuildCameraSnapshot()` falls back to a known
> identity-pose (origin + `-Z` forward, 60° FOV) because
> `Scene.Cameras[0]` has no world transform and
> `RendererPluginContext` does not yet expose an
> `ActiveCameraEntity -> Transform` lookup. Phase 3 ships the pose
> resolution; distance scoring degrades predictably in the meantime.

## Performance characteristics

| Knob | Default | Notes |
| --- | --- | --- |
| `GpuWorkDomain.Gi.BudgetMilliseconds` | `2.0` | Hard ceiling; `TryAdmit` returns `false` once exceeded. |
| `GpuWorkDomain.Gi.EstimatedUnitMilliseconds` | `0.25` | Initial estimate; overridden by `RecordCompletedWork`'s EMA. |
| `GpuWorkDomain.Gi.MaximumUnits` | `8` | Sized to match the time-driven cap (8 × 0.25 = 2.0 ms). Raise `BudgetMilliseconds` in tandem if hardware RT costs change. |
| `MaxProbesPerFrame` const | `8` | Capped at `MaximumUnits` so admission math stays consistent. |
| `DistanceWeight` | `1.0` | Camera-distance priority weight in `DDGIProbePriority.Tuning`. |
| `DistanceFalloffMeters` | `24.0` | Distance scoring window for camera-frustum prioritization. |
| `FrustumContainmentBonus` | `0.5` | Per-probe score bonus when inside the camera frustum. |
| `StalePenaltyPerFrame` | `0.05` | Linear penalty growth per stale frame; cumulative across frames. |
| `StalePenaltyCap` | `1.0` | Cap on the cumulative stale penalty so old probes don't dominate. |
| `DirtyLightBoost` | `4.0` | Multiplier for probes within range of a recently-changed light. |
| `DirtyLightBaseBoost` | `0.5` | Base-amount added when a dirty-light is in range. |

The budget gate cooperates with the existing
`RecordFrameGpuTime` feedback loop, so heavy `Gi` frames reduce
admission over time.

## Render-flow lifecycle

`DDGIRendererPlugin.BuildPlan` runs once per frame after the host
has resolved the scene + content root. The sequence below is the
canonical resolution order — reordering these steps risks the
one-frame `SparseLayoutReady` flicker that previously caused
PbrPass to silently fall back to no-DDGI shading and the editor
probe overlay to read stale SSBO tails.

1. **`EnsureAtlas`** — allocate persistent atlas and storage buffers and
   register the two atlas textures in the shared bindless heap.
2. **`EnsureVolumeLayout`** — initialise only the CPU volume metadata; no
   probe positions or grid entries are uploaded.
3. **Placement pass** — runs every frame before PBR. It clears the GPU
   counter and indirect draw args, accepts empty volume cells, and atomically
   allocates dense probe slots. When a scene TLAS is available it also runs
   free-space relocation tests; without a TLAS it keeps the volume populated
   so debug visualization and sky-only GI remain available while geometry is
   streaming.
4. **Light upload** — refresh the light snapshot/tree used by probe rays.
5. **Update pass** — launches `MaxProbesTotalBudget` groups. Each group reads
   the GPU counter and updates one live probe; inactive groups return before
   touching the atlas. With a TLAS, rays gather visibility and direct light;
   without one, the same GPU kernel writes a sky/ambient fallback into the
   atlas instead of aborting the pass. This keeps scheduling and active-count
   ownership on GPU.
6. **PBR sampling** — reads the GPU-written grid and position buffers through
   device addresses and samples the SH/visibility atlases.
7. **Debug pass** — uses the GPU indirect draw argument buffer, so it never
   derives its vertex count from a CPU probe count and never renders zeroed
   origin tails.

> **Hot-reload caveat:** `SceneGraph.GetHashCode()` is reference
> identity. If the host scene-graph wrapper is recreated every
> frame, the fingerprint will fire `Reset ≡` re-upload ≡ re-run
> placement every frame, thrasing the Gi budget. Hashes should
> be derived from observed scene content (mesh-AABB count,
> entity count) once the renderer-free scene surface lands.

## Consumer-side bindings

PbrPass wires the DDGI atlas into the `ScenePushData` extension
once `_ddgiAtlas.IsSparseLayoutReady` is true:

* `push.DDGIAtlasParams.x` = irradiance bindless slot,
  `push.DDGIAtlasParams.y` = visibility bindless slot,
  `push.DDGIAtlasParams.z` = probe grid resolution,
  `push.DDGIAtlasParams.w` = ready flag (`1.0` when sampling
  should engage, `0.0` otherwise).
* `push.DDGIProbePositions` + `push.DDGIGridToProbeIndex` carry
  the device addresses of the atlas's sparse SSBOs so the shader
  can dereference them via the push constant root.
* `push.DDGIOriginAndCountZ.xyz` = probe-volume origin,
  `.w` = probe grid Z resolution.
* `push.DDGIExtentAndFlags.xyz` = probe-volume half-extents,
  `.w` = `MaxProbesPerFrame` ceiling.

The sampling shader (`ddgi_sampling.slang`, included into
`pbr.slang` behind `#ifdef DDGI_PLUGIN`) walks the 8-cell
trilinear corner of the shading point's coarse cell, calls
`LookupSparseProbeIndex(cell, gridCount)` to recover the
canonical atlas slot, and skips any cell whose indirection entry
is `-1` (placement-rejected or uninitialised). The hard-coded
sparse-index math that previously lived inline is gone — that
math collapsed cells onto a fake stepping-2 grid and dropped the
entire PBR GI term to zero. Sampled SH2 coefficients are weighted
by trilinear interpolation, distance falloff, and the canonical
Chebychev visibility weighting before being added to the
Lo-summed direct-lighting term.

## Persistent-resource barriers

The DDGI atlas is owned by the plugin but imported into the render graph with
stable `ResourceHandle` values. Placement declares UAV writes for probe
positions, grid indirection, the active counter, and indirect draw arguments.
The draw-argument buffer is a 16-byte non-indexed Metal indirect command
(`[vertexCount, instanceCount, firstVertex, firstInstance]`), initialized with
`instanceCount = 1` and incremented by the placement kernel. Update declares
reads of placement/light buffers and UAV writes to irradiance
and visibility. PBR and the debug overlay declare their corresponding reads.
The compiler therefore emits producer-to-consumer barriers while excluding
these persistent resources from transient heap aliasing.

`Renderer` binds the current provider's actual RHI buffers and textures before
each graph execution and removes bindings when the provider disappears. The
executor maps graph states explicitly to native RHI states; the numeric enum
values are intentionally different. `Game.Tests/RenderGraphExternalResourceTests.cs`
covers barrier inference, aliasing exclusion, invalid handles, and state
mapping.

## Runtime fallback constraint

The placement and update shaders retain the `sceneTlas` declaration for the
hardware-raytracing path and select it with a uniform `UseSceneTlas` flag. The
host skips the AS bind when no TLAS exists. This is valid on the current Metal
path because the no-TLAS branch performs no AS access; a future backend must
preserve that descriptor-lifetime rule or provide a dedicated no-RT shader
variant.

## Cross-references

- `engine-spec.md` §4 (RHI commitment to Metal RT first;
  DDGI gather phase uses inline ray queries via
  `BindAccelStruct`).
- `docs/renderer/render-graph.md` (pass lifecycle; the Phase-3
  `DDGIProbeUpdatePass` will be an async compute pass with
  `ResourceState.UnorderedAccess` on the probe atlas).
- `docs/asset-pipeline/tags.md` will gain a `DDGI_PROBE_VOLUME_V1`
  tag when serialised volumes ship.

## Phase status

| Phase | Status |
| --- | --- |
| CPU scaffolding (volume + priority + light BVH) | landed (`54df63c`) |
| Budget gate + plugin lifecycle + sampler + docs (this doc) | landed (Phase 2) |
| Probe-position debug visualization | landed (Phase 2.5) |
| Metal RT gather + SH projection + parallel `[NumThreads(32,1,1)]` dispatch + timing feedback | landed (Phase 3) |
| Two-bounce cascade | pending (Phase 4) |

## Debugging — probe visualisationOnce `renderer.ddgi` is enabled, the editor's debug-view dropdown
includes **DDGI Probes**. Selecting it overlays the GPU-owned probes in
the active `DDGIProbeVolume`:

* **Topology:** an octahedron uses 24 indexed vertex IDs per probe;
  the indirect command's vertex count is accumulated by the placement
  kernel.
* **Depth:** pipeline-level depth testing is disabled so probes remain
  visible behind geometry, which is useful for diagnosing placement and
  volume coverage.
* **Wire-in:** `DDGIRendererPlugin` registers the debug pass as a post-pass
  and the pass reads persistent probe positions plus GPU indirect draw args.
  It does not upload a CPU probe list each frame.
* **Fallback visibility:** if no scene TLAS is available, placement accepts
  volume cells directly and the debug pass still renders their positions;
  update writes sky/ambient atlas data until ray-tracing geometry becomes
  available.
* **Camera:** the pass uses `TryGetActiveCameraData(width, height, …)` to
  obtain the active view-projection matrix.


[clustered]: ../../Plugins/Renderer.Clustered/ClusteredRendererPlugin.cs
[registry]:  ../../engine_cs/Engine.Renderer/DDGI/DDGIVolumeRegistry.cs
