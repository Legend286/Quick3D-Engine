# PBR Render Pipeline & GPU Culling

**Purpose**: Implements the main forward+ physical-based rendering (PBR) pipeline alongside an early GPU-driven frustum culling mechanism.

## Render Passes
- `PbrPass`: Main rendering pass. Issues compute shader-driven frustum culling, generates indirect draw commands, and renders bindless geometry with PBR materials.
- `GridPass`: Renders the editor wireframe infinite/fade grid. Runs concurrently with or after the PBR pass.
- `ImGuiPass`: Renders UI overlays.

## Shaders
- `pbr.slang`: Forward renderer processing bindless geometry, textures, and applying basic PBR models (to be expanded to full cluster-based Forward+).
- `cull.slang`: Compute shader responsible for AABB-frustum intersection checks. Emits multi-draw indirect `RhiDrawCmd` arrays.
- `grid.slang`: Line-list rendering of a standard 3D grid.

## Data Structures
Uses `PbrPushData` to send buffer addresses (materials, instances, models) globally via push constants. Eliminates per-draw CPU binding overhead.
