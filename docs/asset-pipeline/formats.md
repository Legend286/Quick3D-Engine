# Asset Pipeline - Formats

> **TODO(asset):** Per-format tag list + reader contract. See [tags.md](tags.md) for the tag-format dual-backend philosophy.

Cooked formats:

- `.mdl` — meshes + sidecar meta.
- `.msh` — versioned static (`MSH1`) or skinned (`MSH2`) mesh payload.
- `.anim` — versioned JSON skeleton and dense local-TRS animation clips.
- `.ktx2` — Basis Universal transcode target.
- `.audio` — miniaudio container.
- `.scene.json` — JSON scene graph.

Version 1 `.anim` files contain `version: 1`, a `skeleton` object, and one or
more named `clips`. Skeleton bones use stable array indices, a `parent` index,
and optional `translation`, `rotation` quaternion, and `scale` arrays. The
loader derives hierarchy depth and defaults missing inverse-bind matrices to
identity. Each clip stores `sample_rate`, `duration`, `looping`, and dense
`frames`; every frame must contain exactly one local transform per skeleton
bone. Dropping a `.mdl` into the editor loads a sibling `.anim` with the same
basename, registers its first clip, attaches an `AnimatorComponent`, and draws
the reference-pose hierarchy in the editor foreground overlay. When a scene is
saved, an entity with an `AnimatorComponent` records its explicit `animation`
source on the model reference, including paused animators. Scene reload only
loads that explicit source; legacy model references without `animation` remain
static even when a sibling `.anim` exists.

`MSH1` stores the existing 16-byte header, 48-byte static render vertices, and
indices. `MSH2` uses the same header with skinned vertices stored as 80-byte
records: position, normal, UV, tangent, four joint indices, and four weights.
The cooker emits `MSH2` only for a primitive attached to the imported glTF skin
with valid `JOINTS_0` and `WEIGHTS_0`; static or unsupported primitives remain
`MSH1`. `MeshLoader` retains the source stream, classifies the mesh as
`Deforming`, and the compute animation pass writes the existing 48-byte
visibility vertex stream before raster visibility, shadows, and path tracing
consume it. Skinned source vertices stay in bind-pose space; the per-part
`skinned_output_offset` metadata is applied after skinning so inverse-bind
matrices remain correct.

Minimal example:

```json
{
  "version": 1,
  "skeleton": {
    "root_bone": 0,
    "bones": [
      { "name": "root", "parent": -1,
        "translation": [0, 0, 0],
        "rotation": [0, 0, 0, 1],
        "scale": [1, 1, 1] }
    ]
  },
  "clips": [
    { "name": "idle", "sample_rate": 30, "duration": 1,
      "looping": true, "frames": [[{}]] }
  ]
}
```

`MeshLoader` derives a conservative local sphere directly from mesh vertex
positions using an extrema seed and Ritter expansion. `ModelLoader` merges
those part spheres for a whole-model sphere, or returns one stable part sphere.
Part AABBs are only a fallback for legacy meshes without sphere data. Editor
icons, live previews, model-part children, and future LOD inspectors share the
geometry sphere so yaw does not invalidate camera fitting.

`ModelLoader.ReadDefinition` reads `.mdl` part names, mesh paths, materials,
and bounds without allocating GPU resources. `ModelLoader.SelectPart` creates
a one-part runtime model while preserving `SourcePath`, `SourcePartIndex`, and
the skeleton/animation sidecar paths. Scene model references serialize the
optional source `part_index`; absence means the complete imported model.

When a skeleton is imported, the cooker embeds `skeleton` and (when
animations are selected) `animation` fields into the `.mdl` JSON so the
model-to-sidecar association is explicit. Runtime and editor lookups prefer
these embedded paths and fall back to the legacy same-basename
`Path.ChangeExtension(mdl, ".anim")` convention for assets cooked before the
fields existed. This decouples the animation sidecar from the cook's root-node
naming: the `.mdl` is named after the glTF root node while the `.skel`/`.anim`
are named after the source file stem, so a basename guess silently failed for
divergently-named imports.

`PrimitiveMeshFactory` writes `MESH_V1`-compatible `MSH1` `.msh` files for UV
spheres, tori, boxes, and subdivided planes. Writes use a same-directory
 temporary file, flush file contents to stable storage, and atomically replace
the destination. Imported deforming meshes use the cooker-owned `MSH2` layout
above.

Scene saves belong under `Content/scenes/` and store model sources relative to
`Content/`; absolute model paths inside the content root are normalized during
save. `SceneLoader` prefers the `Content/scenes/` location for named scenes and
repairs legacy scene files with no render passes by inserting the canonical PBR
pass. This prevents a save/reload cycle from falling through to a clear/grid-only
background while models and lights are otherwise present in the JSON.

The current scene schema stores camera clip ranges but not the editor camera
transform. Reloading restores scene assets and lights, while the editor camera
continues to use its existing viewport default until camera-pose persistence is
added.

See [`engine-spec.md` §4](../../engine-spec.md).
