# Asset Pipeline - Formats

> **TODO(asset):** Per-format tag list + reader contract. See [tags.md](tags.md) for the tag-format dual-backend philosophy.

Cooked formats:

- `.mdl` — meshes + sidecar meta.
- `.ktx2` — Basis Universal transcode target.
- `.audio` — miniaudio container.
- `.scene.json` — JSON scene graph.

`PrimitiveMeshFactory` writes `MESH_V1`-compatible `.msh` files for UV spheres,
tori, boxes, and subdivided planes. Writes use a same-directory temporary file,
flush file contents to stable storage, and atomically replace the destination.

See [`engine-spec.md` §4](../../engine-spec.md).
