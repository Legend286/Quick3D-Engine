# Commit Conventions

Mirror of [`AGENTS.md` §2](../AGENTS.md). This doc exists so non-agent writers (humans) can read the conventions independently.

## Format

```
<type>(<scope>): <subject>      ≤ 72 chars, imperative, no period.
<BLANK LINE>
<body>                          explain WHY, not what.
<BLANK LINE>
<footer>                        BREAKING CHANGE: markers, issue refs.
```

## Types

`feat`, `fix`, `docs`, `refactor`, `perf`, `test`, `build`, `chore`, `revert`.

`build(deps):` is the standard form when bumping third-party library versions.

A commit that breaks the C ABI **must** carry `BREAKING CHANGE:` in the footer and bump `ENGINE_ABI_VERSION` in the same commit.

## Scopes (engine mirror)

| Scope | Domain |
| --- | --- |
| `rhi` | RHI C API, Metal/Vulkan backends, frame graph. |
| `ecs` | FLECS integration. |
| `scene` | Scene JSON, level editor. |
| `asset` | Asset pipeline + format tags. |
| `cook` | `Cook/` CLI specifically. |
| `editor` | Avalonia shell + ImGui overlay. |
| `mat` | Material editor. |
| `model` | Model viewer + `.mdl` format. |
| `particle` | Particle editor + sim. |
| `physics` | Jolt bindings, VHACD. |
| `audio` | miniaudio + Steam Audio. |
| `net` | Networking. |
| `save` | Save framework. |
| `cinematic` | Timeline editor. |
| `loc` | Localization + accessibility. |
| `ai` | Navmesh, behavior trees. |
| `steam` | Steamworks. |
| `prof` | Profiling, Tracy. |
| `crash` | Crash reporter. |
| `console` | Dev console + Roslyn REPL. |
| `input` | `input.json`. |
| `eeproj` | Project descriptor. |
| `build` | CMake + dotnet build. |
| `ci` | CI workflows. |

## Granularity

One logical change per commit. Refactors go in their own commit before a feature so diffs stay clean.
