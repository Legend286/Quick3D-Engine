# `third_party/`

Vendored dependencies. Brought in via CMake `FetchContent` (preferred) or shallow submodule clones when network is restricted.

Vendored libraries:

- FLECS, cglm, MikkTSpace, tinygltf, Basis Universal, VHACD, xatlas, Jolt Physics, miniaudio, Steam Audio (later).

Pin to specific commits per AGENTS.md §2.2. Bumps via `build(deps):` commits.
