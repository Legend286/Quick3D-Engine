# Module Layout

> **TODO(architecture):** Per-module responsibility + file map.

The C backend (`engine_c/`) exposes a stable ABI in `engine.h`. C# modules (`engine_cs/Engine.<Module>/`) consume the ABI via P/Invoke. The editor (`Editor/`) hosts both Avalonia + ImGui panels.

| Folder | Owner |
| --- | --- |
| `engine_c/` | C/C++ engine core, RHI. |
| `engine_cs/` | C# engine modules. |
| `Editor/` | Avalonia app + ImGui panels. |
| `Game/` | User hot-reload assembly. |
| `Cook/` | Standalone cook CLI. |
| `Content/` | Project assets. |
| `third_party/` | Vendored deps via CMake FetchContent. |
| `out/` | Build outputs (gitignored). |

See [`engine-spec.md` §22](../../engine-spec.md).
