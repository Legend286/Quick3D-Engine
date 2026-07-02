# AGENTS.md

> AI-agent instructions for the Quick 3D Engine (a Source-Inspired Game Engine).
> This file is read by automated coding assistants (Codebuff, Cursor, Aider, Copilot Workspace, etc.) at the start of every session. Honor its rules on every change.

---

## 1. Read-first

Before doing anything else in a session, an agent should be familiar with:

- `engine-spec.md` — architecture, locked decisions, deferred features, risks.
- `.eeproj/README.md` once it exists — project descriptor rules.
- `docs/` — module-level documentation lives here. **Write new docs into this folder.**

If any of those files contradict this document, **`engine-spec.md` wins** for architectural facts; this file wins for workflow and policy.

---

## 2. Commits — Conventional Commits, strict

All commit messages follow the [Conventional Commits 1.0.0](https://www.conventionalcommits.org/) specification, with the project-specific scope taxonomy below.

### 2.1 Format

```
<type>(<scope>): <subject>
<BLANK LINE>
<body — explain WHY, not what>
<BLANK LINE>
<footer>
```

Rules:

- **Subject** ≤ 72 characters. Imperative mood. No trailing period.
- **Body** wrapped at 72 columns. Explains motivation, design choice, rejected alternatives. The diff shows *what*; the message shows *why*.
- **Footer** carries `BREAKING CHANGE:` markers and reference IDs (`Refs: #123`).

### 2.2 Type vocabulary

| Type | Use for |
| --- | --- |
| `feat` | New user-visible capability. |
| `fix` | Bug repair (behavior was wrong). |
| `docs` | Changes to `docs/`, `engine-spec.md`, this file, or README. **Not** for inline source comments. |
| `refactor` | Code change that neither fixes a bug nor adds a feature. |
| `perf` | Performance improvement with a measurable before/after summary in the body. |
| `test` | Add or fix unit / integration / cook tests. |
| `build` | Build system, dependency, or CI changes. |
| `chore` | Tooling, formatting, version bumps that don't affect runtime. |
| `revert` | Roll back a prior commit; body explains the situation. |

`build(deps):` is the standard form when bumping third-party library versions.

A commit that breaks the C ABI (changes to `engine.h` exports) **must** carry `BREAKING CHANGE:` in the footer AND bump `ENGINE_ABI_VERSION` in `engine.h` in the same commit.

### 2.3 Scope taxonomy — project mirror

The scope should match the engine's module breakdown so commit history reads as a per-subsystem changelog:

| Scope | Domain |
| --- | --- |
| `rhi` | RHI C API surface, Metal/Vulkan backends, frame-graph barriers. |
| `ecs` | FLECS integration, component schema, system scheduling. |
| `scene` | Scene JSON format, entity placement, level editor. |
| `asset` | Asset pipeline, cook step, format tags. |
| `cook` | The `Cook/` CLI specifically. |
| `editor` | Avalonia shell, ImGui overlay, docking, asset browser. |
| `mat` | Material editor + `.mat` JSON schema. |
| `model` | Model viewer + `.mdl` format. |
| `particle` | Particle editor + GPU/CPU sim. |
| `physics` | Jolt bindings, VHACD, fixed-step solver. |
| `audio` | miniaudio + Steam Audio. |
| `net` | Networking, authoritative server, rollback client. |
| `save` | Save game / snapshot. |
| `cinematic` | Timeline editor. |
| `loc` | Localization, accessibility. |
| `ai` | Navmesh, behavior trees. |
| `steam` | Steamworks integration. |
| `prof` | Profiling, Tracy integration. |
| `crash` | Crash reporter + diagnostics. |
| `console` | Dev console + Roslyn REPL. |
| `input` | `input.json` schema + bindings. |
| `eeproj` | Project descriptor. |
| `build` | CMake, dotnet build wiring, `third_party/`. |
| `ci` | CI workflows, pre-commit hooks. |

Scopes are required, not optional. Use `chore(misc):` only when none of the above apply, and try hard not to.

### 2.4 Commit granularity

- **One logical change per commit.** If a feature + its tests + its docs constitute one logical change, they belong in one commit.
- A feature spanning multiple subsystems MUST split into multiple `feat(<scope>):` commits in dependency order, with an overarching tracking commit message body that lists them.
- Never bundle a refactor with a feature. Refactors go first so the feature commit shows a clean diff.

---

## 3. Documentation — write docs for everything

### 3.1 Rule

Every public API surface, every cook-step tool, every editor tool, every subsystem must have a corresponding markdown document under `docs/`. **No public symbol goes undocumented.**

### 3.2 Documentation layout

```
docs/
├── architecture/
│   ├── overview.md
│   ├── module-layout.md
│   └── thread-model.md
├── rhi/
│   ├── api.md                  (engine.h reference, mirrored)
│   ├── metal.md                (Metal backend notes)
│   └── vulkan.md               (stub until implemented)
├── ecs/
│   └── components.md
├── asset-pipeline/
│   ├── formats.md
│   ├── cook.md
│   └── tags.md
├── editor/
│   ├── tools.md
│   └── extensions.md           (Avalonia plugins, custom panels)
├── physics/
│   └── jolt-binding.md
├── audio/
│   └── steam-audio.md
├── networking/
│   └── rollback.md
├── save/
│   └── snapshot-format.md
├── ai/
│   └── navmesh.md
├── cinematic/
│   └── timeline.md
├── localization/
│   └── tables.md
├── profiling/
│   └── tracy.md
├── crash/
│   └── dump-format.md
├── console/
│   └── roslyn-repl.md
├── project-descriptor.md       (.eeproj)
├── commit-conventions.md       (mirror of §2 of this file)
└── style.md                    (naming + formatting rules)
```

If a folder listed above doesn't have a markdown file yet, it doesn't exist yet. **Create the doc when you create the code that needs it.** Do not defer.

### 3.3 Per-feature doc requirements

Every new feature ships with a doc that has:

1. **Purpose** — one paragraph max.
2. **Public API surface** — function/type names with one-line descriptions.
3. **Usage example** — minimal, copy-pasteable.
4. **Performance characteristics** — when relevant.
5. **Cross-references** — links to related docs and to `engine-spec.md`.

### 3.4 Editing existing docs

When code changes, the matching doc updates **in the same commit** as the code change, unless the doc update is large — in which case split into a follow-up `docs(<scope>):` commit referencing the prior commit hash.

### 3.5 README files

- Top-level `README.md`: project pitch + status table (which subsystems are functional vs. stubbed vs. deferred).
- Each folder under `engine_c/`, `engine_cs/`, `Editor/`, `Game/`, `Cook/`, `Content/` has its own `README.md` describing contents at a high level.
- Each top-level script/CLI has a small `README.md` next to it documenting flags and inputs.

---

## 4. Comments — chatty banned, structured allowed

### 4.1 The rule

Inline **chatty** comments are banned. They are noise and they rot. **All rationale belongs in `docs/`**.

```c
// BAD — chatty
// Loop over each entity and apply gravity.
// Update the position by adding velocity * deltaTime.
for (int i = 0; i < count; i++) {
    position[i] += velocity[i] * dt;
}
```

A reader who needs to understand the WHY of this loop reads `docs/physics/jolt-binding.md` (or whatever subsystem owns it). The code itself must read like prose because names and types self-describe.

### 4.2 What IS allowed

- **SPDX / license headers** at the top of source files. Required for engine code; format:
  - C: `/* SPDX-License-Identifier: MIT */`
  - C#: `// SPDX-License-Identifier: MIT`
  - CMake: `# SPDX-License-Identifier: MIT`
- **Structured Doxygen / XML doc comments** on public C exports and C# public types/methods:
  ```c
  /** Create a render pipeline. See docs/rhi/api.md#create_pipeline. */
  ENGINE_API RhiPipeline* rhi_create_pipeline(RhiDevice*, const RhiPipelineDesc*);
  ```
  ```csharp
  /// <summary>
  /// Loads a scene from disk into the active ECS world.
  /// </summary>
  /// <remarks>See docs/scene/json-format.md for schema.</remarks>
  public SceneHandle LoadScene(string path);
  ```
- **TODO markers** that are tied to a tracking ticket: `// TODO(#423):` — reference an issue, not freeform text. After the issue is fixed, the TODO and the comment both go.
- **Region markers** in shaders (`// MARK: Begin Light Loop`) are acceptable as structural dividers.

### 4.3 Corner cases (file type → policy)

| File type | Comment policy |
| --- | --- |
| `*.c`, `*.h` (engine C) | Doxygen on public exports only. No chatty inline. |
| `*.cpp` (Jolt C++ wrapper) | Same as C; chatty banned. |
| `*.cs` (engine + Game) | XML doc on public types/methods only. |
| `*.shader`, `*.metal`, `*.hlsl`, `*.glsl` | No chatty inline. `// MARK:` dividers allowed. No `// this does X` comments. |
| `*.csproj`, `*.cmake` | No comments except SPDX header (CMake only). |
| `*.json` (`*.mdl` sidecar, scenes, materials, `.eeproj/*`) | Comments banned inside data files (breaks parsers). Use `docs/` markdown. |
| `*.yaml`, `*.toml` config | No comments. Schema is documented in `docs/`. |
| `*.sh`, `*.ps1`, Python tooling | SPDX header only. No inline chatty. |
| Test files (`*_test.cs`, `test_*.cpp`) | Doxygen on test helpers OK. Test names should be self-describing (`ApplyForce_ZeroMass_DoesNothing`). |
| Build scripts (`build.sh`, `RunEditor.sh`) | SPDX header only. |

### 4.4 The "spirit" test

> If removing the comment would not change what the code *does* — the comment is unnecessary. If removing it would not change what a future reader *understands about why* — that understanding belongs in `docs/`.

---

## 5. File integrity — atomic writes + checksums + rollback

### 5.1 Atomic writes

Any code path that writes a file in user-data space — editor saves, configuration writes, cook outputs, save game writes — **must** use the atomic write pattern:

1. Write to `<path>.tmp` in the same directory.
2. `fsync()` the `.tmp` file (engine code) / `FileStream.Flush(true)` (C#).
3. `rename(<path>.tmp, <path>)` — atomic on POSIX, atomic on Windows via `MoveFileEx(MOVEFILE_REPLACE_EXISTING)`.
4. On the editor save path, fsync the directory after renaming if the OS supports it.

This applies to:

- `.eeproj/*.json` saves.
- `scene.json`, `*.mat`, `*.mdl`, `*.ktx2` cook outputs.
- `editor.local.json`, `input.json` runtime remapping saves.
- Save game framework writes.
- Any user-saved C# script compile artifacts produced outside `dotnet build`.

### 5.2 Checksum manifest

The cook step emits `out/cook/<project>/<config>/manifest.json` containing cryptographic hashes of every cooked artifact:

```json
{
  "version": 1,
  "cook_timestamp_utc": "2026-06-25T12:34:56Z",
  "engine_abi_version": 1,
  "entries": {
    "models/test/test.mdl":     { "sha256": "...", "size_bytes": 12345, "tag": "MESH_V1_LZ4" },
    "textures/test.ktx2":       { "sha256": "...", "size_bytes": 67890, "tag": "KTX2_BASIS_UASTC" },
    "scenes/main_menu.scene.json": { "sha256": "...", "size_bytes": 1024,  "tag": "SCENE_V1" }
  }
}
```

The game runtime verifies the manifest against the cooked-artifact directory at startup. Mismatches fail loudly with a clear error.

### 5.3 Rollback surface

Three rollback tiers, in order of preference:

1. **Git revert of the saved file** — most user-facing file saves persist as regular git commits.
2. **Editor-side snapshot ring** — the editor maintains `out/.snapshots/<timestamp>/` for the last 5 saves of any editable file. Snapshot on every atomic write. Surfaced in the editor's `File > Recent Snapshots...` menu.
3. **Cook manifest + cook cache** — corrupted cooked output is detected via manifest mismatch; the engine triggers a re-cook of the affected files using the cook cache where possible.

### 5.4 What atomic writes don't cover

Within a single commit to git, multiple files may be created. That is fine; atomicity is per-file. **Do not** use `git add -A` and commit — review the staging area and commit coherent logical changes.

---

## 6. Workflow expectations

### 6.1 Pre-commit checks (in repo, set up by Phase 1)

A `.pre-commit-config.yaml` hooks the following:

- `conventional-commit` style check on the staged message.
- `clang-format` on staged C/C++ files.
- `dotnet format` on staged C# files.
- Trailing-whitespace + final-newline fixers (the editor should already do this).
- Validate JSON files (`*.json`) parse cleanly.

When no `.pre-commit-config.yaml` exists yet (Phase 0/1), agents must run these checks manually before reporting a task complete.

### 6.2 Branch + PR policy

- `main` is always buildable. Direct commits to `main` are reserved for tiny mechanical changes (CHANGELOG bumps, dependency updates). Anything with architectural impact goes via PR.
- PR titles follow Conventional Commits. The squash-merge title becomes the final commit message; the description becomes the body. **Reviewers adjust the PR title before squash-merge if needed.**
- Branches use the convention `feat/<scope>/<short-slug>`, `fix/<scope>/<short-slug>`, `refactor/<scope>/<short-slug>`.

### 6.3 Per-session agent checklist

When an agent is invoked on this repository it should:

1. Read `engine-spec.md`, this file, and the relevant `docs/<scope>/` page.
2. **Plan before code.** For any change touching > 1 file or > 50 lines, write a brief plan, surface it to the user, wait for approval.
3. Stage specific files. `git add -A` is forbidden.
4. Match the commit subject to one of the scopes in §2.3.
5. Update docs in the same commit when applicable.
6. Run formatter + tests for the area you touched.
7. End with a concise summary of what changed.

### 6.4 Building the project

Until cross-platform builds are fully supported, **always use the `scripts/build-mac-app.sh` script** for building the project. Do not use generic `dotnet build` or `cmake` commands directly for the main application build unless specifically working on those systems.

---

## 7. Project-specific reminders

These are not policy, they're pointers to the locked architecture — keep them in mind on every change:

- **C backend / C# userspace.** Public C exports live in `engine.h` and are tagged `ENGINE_API`. ABI version lives in the same file. Bump on breaking change.
- **RHI is authoritative.** The C RHI is the source of truth for all rendering. C# renders through it; do NOT use Veldrid, SkiaSharp-render, or any other C# rendering library.
- **Metal-first, Vulkan-later.** Write RHI code that is Metal-correct AND Vulkan-conceptual. Avoid Metal-only assumptions in any C-level RHI code that has a future Vulkan counterpart. Cross-check against the RHI API surface in `engine-spec.md`.
- **Hot reload.** Anything in `Game/` is hot-reloadable. Anything in `Engine.*` is also hot-reloadable for power users. Generational handles MUST keep cross-references stable across reload.
- **Mesh-shader driven Forward+.** Any new render pass should fit the mesh-shader / cluster-culling model. Avoid hand-rolled legacy vertex-pulling unless absolutely necessary for particle ribbons.
- **Basis Universal textures.** New texture inputs use the Basis Universal cook pipeline. No raw PNG should ever ship as a runtime asset.
- **Tag-format assets.** All cooked binary assets carry a tag (`MESH_V1_LZ4`, `KTX2_BASIS_UASTC`, `AUDIO_OGG`, etc.). Recognize the tag list at cook-time AND at load-time. Adding a new tag = a new format entry in `docs/asset-pipeline/tags.md`.
- **JSON for source-of-truth schema.** Scenes, materials, sidecar metas, project descriptor — all JSON. Binary only for runtime hot-path assets.
- **Folder-organised assets.** Mounts and addons follow the `engine-spec.md` rules; do not invent new content-root conventions without updating `engine-spec.md`.

---

## 8. Quick reference

### 8.1 Sample commits

```
feat(rhi): implement mesh-shader culling kernel

Adds task shader + mesh shader primitives to the RHI surface.
Required for Forward+ per engine-spec.md §5. Falls back to
vertex-shader path on hardware without mesh-shader support
(Metal < 3 or Vulkan < 1.3).

Refs: engine-spec §5.2, docs/rhi/metal.md
```

```
fix(physics): jolt body not collision-layer-respected

Body was created against world layer mask without honoring
the project's default_layer_count > 16, so high-bit layers
were silently dropped. Now reads modules.json and clamps.

Closes #87
```

```
build(deps): bump basis-universal to v1.50.0

Required for UASTC HDR support in upcoming HDR water pass.
Verified cooked outputs unchanged on existing test assets.
```

```
feat(eeproj)!: external mount path field renamed to 'mount_path'

BREAKING CHANGE: External mount entries changed from
'path' to 'mount_path' to disambiguate from addon paths.
ENGINE_ABI_VERSION bumped to 2.

BREAKING CHANGE: external mount schema renamed 'path' -> 'mount_path'
```

### 8.2 Forbidden patterns

- **Inline chatty comments in source** — write a doc instead.
- **`git add -A`** — stage by intent.
- **C# rendering libraries in the renderer path** — go through the C RHI.
- **Raw PNG / OGG / glb files in the runtime asset tree** — only cooked Basis/Audio/MDL.
- **Allocating in hot paths with `malloc`/`free`** — frame allocator + bump allocators.
- **Hardcoded paths in code** — paths go through `Engine.Assets` or VFS.
- **Raw pointers across threads / hot reloads** — generational handles only.
- **Committing `editor.local.json`** — gitignored.
- **Using Git LFS** — Git LFS is removed and disabled. Use standard git for all assets.

### 8.3 Minimum bar for "done"

A task is complete only when:

1. The code change compiles and existing tests pass.
2. The matching doc under `docs/` is updated or created.
3. The staged commit message complies with §2 (Conventional Commits + scope).
4. Conventional checks (`clang-format`, `dotnet format`, JSON validation) pass on staged files.
5. The summary told to the user is ≤ 5 bullet points and references affected docs.

---

_End of AGENTS.md. If a rule here ever conflicts with what the user asks for, the user's explicit instruction wins for that one task — but `engine-spec.md` should be updated to reflect the permanent decision._
