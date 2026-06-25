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
