# End Engine

A Source-Engine-inspired game engine. C backend + C# userspace, Metal-first RHI with Vulkan-later, FLECS for gameplay, Avalonia + ImGui editor suite.

> **Status: Phase 0 (foundations).** Code is in initial scaffolding. See `engine-spec.md` for the complete design.

## Subsystem status

| Subsystem | Status | Doc |
| --- | --- | --- |
| Logger (Phase 0) | Stubbed API | [engine-log](docs/architecture/engine_log.md) |
| RHI (Metal) | not started | [rhi/api](docs/rhi/api.md) |
| ECS (FLECS) | not started | [ecs/components](docs/ecs/components.md) |
| Asset pipeline | not started | [asset-pipeline/cook](docs/asset-pipeline/cook.md) |
| Editor (Avalonia) | not started | [editor/tools](docs/editor/tools.md) |
| Renderer (Forward+, Mesh-shader) | not started | [rhi/metal](docs/rhi/metal.md) |
| Physics (Jolt) | not started | [physics/jolt-binding](docs/physics/jolt-binding.md) |
| Audio (miniaudio + Steam Audio) | not started | [audio/steam-audio](docs/audio/steam-audio.md) |
| Networking (authoritative + rollback) | not started | [networking/rollback](docs/networking/rollback.md) |

## Building

(Late Phase 0 / Phase 1.) Run:

```sh
cmake -S . -B out/build
cmake --build out/build
dotnet build
```

## Editing this engine

Read [`AGENTS.md`](AGENTS.md) before making changes. It locks the workflow rules: Conventional Commits + scope taxonomy, chatty comments banned, atomic writes, file integrity machinery.

The architectural source of truth is [`engine-spec.md`](engine-spec.md).
