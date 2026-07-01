# Asset Pipeline Cooker

**Purpose**: A C++ offline tool (`engine_cook`) that reads glTF/PNG assets from `Content/src` and outputs optimized, runtime-ready binary files to `Content/assets`.

## Core Features
- **tinygltf**: Parses `.gltf`/`.glb` files, extracts geometry, translates attributes, and computes normals/tangents.
- **basisu**: Encodes textures into Basis Universal UASTC compressed format for optimized VRAM usage and loading times.
- **Manifest**: Emits `manifest.json` with cryptographic hashes to avoid redundant recooks and to verify asset integrity.

## Integration
The cooker is compiled as part of the `CMakeLists.txt` step and is automatically executed to update `Content/assets/` on build.
