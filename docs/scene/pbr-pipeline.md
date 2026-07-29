# PBR Render Pipeline & GPU Culling

**Purpose**: Implements the main forward+ physical-based rendering (PBR) pipeline alongside an early GPU-driven frustum culling mechanism.
See [Scene Lights](lights.md) for authored point/spot/directional light
round-tripping through the editor and scene JSON.

## Render Passes
- `PbrPass`: Main rendering pass. Issues compute shader-driven frustum culling, clustered light assignment, indirect draw generation, and bindless PBR geometry rendering.
- `GridPass`: Renders the editor wireframe infinite/fade grid. Runs concurrently with or after the PBR pass.
- `ImGuiPass`: Renders UI overlays.

## Shaders
- `pbr.slang`: Forward renderer processing bindless geometry, textures, clustered light records, and Disney PBR material evaluation.
- `cull.slang`: Compute shader responsible for AABB-frustum intersection checks. Emits multi-draw indirect `RhiDrawCmd` arrays.
- `cluster_lights.slang`: Compute shader assigning scene lights into fixed-size per-cluster light-index lists.
- `grid.slang`: Line-list rendering of a standard 3D grid.

## Data Structures
Uses `PbrPushData` to send buffer addresses (materials, instances, models) globally via push constants. Eliminates per-draw CPU binding overhead.

## Clustered Forward+ Notes
- Cluster records are ordinary RHI storage buffers referenced through `ScenePushData`.
- The first implementation uses 32x32 screen tiles, 16 depth slices, and fixed 64-light slots per cluster.
- Directional lights are assigned to every cluster. Point lights use sphere-vs-cluster tests; spot lights use finite-cone tests against cluster corners, edges, caps, and axis intervals.
- A later RHI count-buffer/compaction pass can replace fixed slots without changing the PBR fragment interface.
