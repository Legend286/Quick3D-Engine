# Asset Pipeline - Formats

> **TODO(asset):** Per-format tag list + reader contract. See [tags.md](tags.md) for the tag-format dual-backend philosophy.

Cooked formats:

- `.mdl` — meshes + sidecar meta.
- `.ktx2` — Basis Universal transcode target.
- `.audio` — miniaudio container.
- `.scene.json` — JSON scene graph.

`MeshLoader` derives a conservative local sphere directly from mesh vertex
positions using an extrema seed and Ritter expansion. `ModelLoader` merges
those part spheres for a whole-model sphere, or returns one stable part sphere.
Part AABBs are only a fallback for legacy meshes without sphere data. Editor
icons, live previews, model-part children, and future LOD inspectors share the
geometry sphere so yaw does not invalidate camera fitting.

`ModelLoader.ReadDefinition` reads `.mdl` part names, mesh paths, materials,
and bounds without allocating GPU resources. `ModelLoader.SelectPart` creates
a one-part runtime model while preserving `SourcePath` and `SourcePartIndex`.
Scene model references serialize the optional source `part_index`; absence
means the complete imported model.

`PrimitiveMeshFactory` writes `MESH_V1`-compatible `.msh` files for UV spheres,
tori, boxes, and subdivided planes. Writes use a same-directory temporary file,
flush file contents to stable storage, and atomically replace the destination.

See [`engine-spec.md` §4](../../engine-spec.md).
