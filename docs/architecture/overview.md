# Architecture Overview

> **TODO(architecture):** One-paragraph pitch + module dependency diagram.

The engine is a C-backed RHI + asset pipeline, a C#-hosted ECS gameplay + renderer, an Avalonia + ImGui editor, and an Avalonia-themed shell for tooling. The baseline ABI lives in `engine_c/engine.h`; per-subsystem modules import it.

For the full architectural source of truth, see [`engine-spec.md`](../../engine-spec.md).

## Module dependency

```
Game -> Engine.* (ECS, Renderer, Audio, ...) -> engine_c (engine.h) -> RHI / FLECS / Jolt / etc.
                                                  |
                                                  v
                                              Docs/ tests
```

## See also

- [module-layout](module-layout.md)
- [thread-model](thread-model.md)
