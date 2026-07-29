# `Content/`

Per-project asset root. Folder layout per [`engine-spec.md` §4.1](../../engine-spec.md):

```
Content/
  models/
  materials/
  textures/
  meshes/shared/
  sounds/
  scenes/
  scripts/
```

Cooked (binary) outputs land under `out/cook/`, not here. Everything under `Content/` is the source of truth that the `Cook/` CLI ingests.

`scenes/shadow_stress.scene.json` is the procedural Forward+ and shadow-cache
performance scene. Its generated meshes are created in the project's
`Content/.cache/procedural-demo` directory on first load.

`shaders/punctual_shadow_cull.slang` batches part culling across admitted
punctual shadow tiles. `shaders/shadow_atlas_preview.slang` visualizes one
depth tile for the render graph inspector without CPU readback.
