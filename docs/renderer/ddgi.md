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
2. A **priority scheduler** that picks which probes to update each
   frame using camera-first + light-changed scoring
   (`DDGIProbePriority`).
3. A **light tree** over punctual lights so probe rays can
   efficiently traverse only relevant emitters
   (`DDGILightBVH`).
4. A **2 ms per-frame budget gate** on the
   `GpuWorkDomain.Gi` admit slot so probe updates can never
   dominate the frame.

The GPU dispatcher (gather kernel + SH projection + atlas write)
lands as a follow-up commit (`Phase 3`); Phase 2 ships the CPU
plumbing that toggle-on makes measurable.

## Public API surface

| Symbol | Kind | Role |
| --- | --- | --- |
| `GpuWorkDomain.Gi` | enum value | 2.0 ms ceiling per frame; per-probe cost tracked via `RecordCompletedWork`. |
| `DDGIProbeVolume` | `Engine.Renderer.DDGI` | SPA origin + half-extent AABB + grid resolution; computes per-probe world positions. |
| `DDGIProbePriority` | same | Camera-first + stale + dirty-light priority queue with multi-frame rotation. |
| `DDGILightBVH` | same | CPU binary-volume hierarchy over punctual lights with `GetBoundingSphere(orderedLightIndex)` for shader ray-box tests. |
| `DDGIRendererPlugin` | `Engine.DDGI`, plugin entry | Implements `IRendererPlanPlugin`; runs priority + budget admission each `BuildPlan` call. |
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

1. Iterates `context.Scene.Lights` (a `SceneGraph.Lights` list of
   `LightNode`s with type/position/range/intensity/etc.) into
   `DDGIProbePriority.LightInfluence` records.
2. Calls `DDGIProbePriority.ScheduleProbeUpdates` capped at
   `MaxProbesPerFrame = 8` (matches `GpuWorkScheduler.Gi`
   `MaximumUnits` so admission math is consistent).
3. Admits each scheduled probe to the `GpuWorkScheduler.Gi` slot.
   Admitted probes go on the GPU update list this frame; deferred
   probes stall until the cycle returns to them.
4. Logs `[DDGI] tick=N volumeProbes=P lightsDirty=L scheduling=K
   admitted=A deferred=D` at info level (sampled every 60
   invocations so toggle-on doesn't spam the console) so the
   toggle effect is observable in the editor console.

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
| Metal RT gather + SH projection + atlas write dispatch | pending (Phase 3) |
| Editor visualisation (probe density heat-map) | pending (Phase 4) |
| Two-bounce cascade | deferred (per user choice; tracked separately) |
