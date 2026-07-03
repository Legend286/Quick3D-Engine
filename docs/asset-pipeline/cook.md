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

## Exit codes

| Status | Cause |
| --- | --- |
| 0 | Success — all textures + materials + meshes + scene written, every `.tex` sidecar verified next to its `.ktx2`. |
| 1 | GLTF/GLB load failure (tinygltf parser error). Surviving `<out>/textures/*.tex` are scrubbed via `ScrubOrphanSidecars` so a re-run does not see lying sidecars from a previous incomplete cook. |
| 2 | `basisu` binary not located through any of the resolution paths below. Same scrub applies. |
| 3 | At least one per-texture `ExecuteBasisu` returned empty (basisu crashed, missing/truncated `.ktx2`, threw). The matching `albedo_texture` / `normal_texture` / `rma_texture` keys in the resulting `.mat` JSONs are omitted; surviving sidecars are inspected by `ScrubOrphanSidecars` once more so an OS-level truncation between writes (e.g. ENOSPC, SIGKILL) cannot produce a lying pair. Editor surfaces this as `Import failed (code 3): …`. |

`AssetImportWindow.axaml.cs` reads `process.ExitCode` after `WaitForExitAsync()` and surfaces any nonzero as a status banner with stderr text. The non-zero outcomes above feed the editor's console panel through `Engine.CBindings.Log.Error` so the failure reason reaches the in-app view + `engine.log` regardless of stdout/stderr capture quirks.

## basisu resolution

`engine_cook` does **not** assume `./out/basisu` exists relative to its CWD — most invocations come from the editor, whose CWD is the user's home or `<project>/`. Resolution order:

1. `--basisu-path <abs>` CLI override.
2. Self-discovery: 4-level ancestor walk from `realpath(argv[0])`. At each level, accept the candidate `<dir>/out/basisu` if either `<dir>/engine_cs/` exists (engine-root marker) OR `<dir>` ends in `out/` (cook shipped directly inside an `out/` subdir of the engine root).
3. `$QUICK3D_ENGINE_ROOT/out/basisu`.
4. `./out/basisu` CWD-relative legacy fallback (kept for direct shell use from `<engine>/`).

If all four fail, `engine_cook` exits with status 2 and prints the resolution ladder to `stderr`. Note that `--basisu-path` is passed through `/bin/sh -c` by `std::system`, so shell metacharacters in the path are honored — it is a developer CLI trusting that input.

## Verify-before-sidecar

The texture phase runs `basisu`, then asserts both `std::system` returned 0 **and** the expected `.ktx2` exists at the chosen `tex_out_dir/<base>.ktx2` path with at least **80 bytes** (the Khronos KTX2 base-header size, identifier + 68 bytes of format/dimension/levelCount/etc.). Only on both checks passing is the `.tex` JSON sidecar written. This prevents a lying sidecar (e.g. `format: ktx2` next to no `.ktx2` or next to a stub identifier-only file) from materializing at the loader — which previously produced silent black material binds at runtime when basisu failed for any reason (binary missing, GLIBC mismatch, etc.).

If execution fails for a texture, the matching `albedo_texture` / `normal_texture` / `rma_texture` keys in the resulting `.mat` JSON are **omitted** (rather than pointing at an empty string). The runtime `MaterialLoader` falls back to the material's base-color / default-uniform-color tint in that case so the surface is visibly untextured (a debug signal), not flat-black.

## Scrub-on-exit

Every early-exit path in `main()` (status 1 GLTF load, status 2 basisu unresolvable, status 3 partial cook) calls `ScrubOrphanSidecars(<out>/textures)` **once** before returning. The helper walks every `.tex` in the dir, removes it if its sibling `.ktx2` is missing or smaller than 80 bytes, and logs each removal at WARN level. The per-texture `fail_with_cleanup` lambda inside `ExecuteBasisu` already scrubs the specific texture's sidecar before returning empty; the post-cook walk is a defensive re-check against OS-level truncation between writes.

## Integration

Cook is invoked by the editor's `AssetImportWindow`. The editor does not need to know the basisu path — Cook self-discovers from where the editor's `engine_cook` binary lives in `<engine>/out/`.

## Future

- Atomic writes per AGENTS.md §5.1 (currently direct `std::ofstream`).
- `manifest.json` per AGENTS.md §5.2 (currently not emitted by Cook; a future cook writes cryptographic hashes so the runtime can audit the cooked tree at startup).
- Expose a per-texture UASTC quality-level knob (currently fixed at the basisu default for `-uastc`).
- Switch `std::system` invocation away from `/bin/sh -c` once `--basisu-path` may flow from non-trusted sources.
