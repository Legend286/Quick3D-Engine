# Asset Pipeline Cooker

**Purpose** — A C++ offline tool (`engine_cook` — see `Cook/`) that reads glTF/GLB models and inline or external textures, runs them through Basis Universal, and emits runtime-ready binaries (KTX2 textures + .mdl meshes + .mat materials + .scene.json fixtures).

## Core features
- **tinygltf** — parses `.gltf`/`.glb`, extracts positions / normals / UVs / tangents, computes node transforms.
- **Basis Universal via `basisu`** — encodes every texture into `-ktx2 -uastc` form (vkFormat 157 / ASTC_4x4 + Zstd scheme=3), directly loadable by `Ktx2Loader`. Earlier `-ktx2` defaults to ETC1S+BasisLZ (scheme=1) which the runtime cannot decode; this cook forces the UASTC path.
- **Output layout** — `<out>/models/` (`.mdl` + named `_part_N.msh`), `<out>/models/materials/` (`.mat` JSON), `<out>/textures/` (`.ktx2` + `.tex` sidecar), `<out>/scenes/` (`.scene.json`).

## CLI

```
engine_cook <input.glb|gltf> [out_dir] [-scale x y z] [--basisu-path <abs>]
```

| Flag | Purpose |
| --- | --- |
| `<input.glb\|gltf>` | Source model (positional, required). |
| `[out_dir]` | Where to dump models/textures/scenes/materials. Default = `<input>`'s parent. |
| `-scale x y z` | Multiply all vertex positions. Negative-axis scales flip triangle winding automatically. |
| `--basisu-path <abs>` | Override the `basisu` binary location; see *basisu resolution* below. |

Cook exits with status `0` on success, `1` on GLTF/glb load failure, and `2` if the `basisu` binary cannot be located through any of the resolution paths below.

## basisu resolution

`engine_cook` does **not** assume `./out/basisu` exists relative to its CWD — most invocations come from the editor, whose CWD is the user's home or `<project>/`. Resolution order:

1. `--basisu-path <abs>` CLI override.
2. Self-discovery from `argv[0]`: parent of parent (where `engine_cook` itself lives) + `out/basisu`. Works whether the editor launches the binary directly or via a wrapper when `engine_cook` is shipped at `<engine>/out/engine_cook`.
3. `$QUICK3D_ENGINE_ROOT/out/basisu`.
4. `./out/basisu` CWD-relative legacy fallback (kept for direct shell use from `<engine>/`).

If all four fail, `engine_cook` exits with status 2 and prints the resolution ladder to `stderr`. The editor's `AssetImportWindow` surfaces this through `error.OutputReadToEnd()`.

## Verify-before-sidecar

The texture phase runs `basisu`, then asserts both `std::system` returned 0 **and** the expected `.ktx2` exists at the chosen `tex_out_dir/<base>.ktx2` path with a non-trivial size. Only on both checks passing is the `.tex` JSON sidecar written. This prevents a lying sidecar (e.g. `format: ktx2` next to no `.ktx2`) from materializing at the loader — which previously produced silent black material binds at runtime when basisu failed for any reason (binary missing, GLIBC mismatch, etc.).

If execution fails for a texture, the matching `albedo_texture` / `normal_texture` / `rma_texture` keys in the resulting `.mat` JSON are **omitted** (rather than pointing at an empty string). The runtime loader falls back gracefully from there.

## Integration

Cook is invoked by the editor's `AssetImportWindow`. The editor does not need to know the basisu path — Cook self-discovers from where the editor's `engine_cook` binary lives in `<engine>/out/`.

## Future

- Atomic writes per AGENTS.md §5.1 (currently direct `std::ofstream`).
- `manifest.json` per AGENTS.md §5.2 (currently not emitted by Cook).
- Expose a per-texture UASTC quality-level knob (currently fixed at the basisu default for `-uastc`).
