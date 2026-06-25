# Project Descriptor — `.eeproj`

The `.eeproj` directory declares a project's identity, layout, default subsystems, addon list, and shippable scenes. It is the project's single source of truth.

Mirror of [`engine-spec.md` §25](../../engine-spec.md). Reproduced here so this doc is independently readable.

## File layout

```
<project>/
├── .eeproj/
│   ├── project.json         (text-tracked)
│   ├── scenes.json          (text-tracked)
│   ├── modules.json         (text-tracked)
│   ├── addons.json          (text-tracked)
│   ├── input.json           (text-tracked)
│   ├── locales.json         (text-tracked)
│   └── editor.local.json    (gitignored)
```

Each sub-file carries its own `version` field. Subsystem-decoupled migration.

## Mounts vs addons

- **External mount** — content-only VFS folder. Listed scenes still need to appear in `scenes.json#cooked_scenes`. Hot-reloads live.
- **Addon** — code-level engine plugin. Needs editor restart to reload.

| | Mount | Addon |
| --- | --- | --- |
| Content | Yes | Optional |
| Code hooks | No | Yes |
| Identifier | path (local or `url@commit`) | reverse-DNS + semver |

## Atomic writes + integrity

Every editor save goes through tmp+rename. Cook emits a `manifest.json` with SHA-256 of every cooked artefact. Three rollback tiers: git revert, editor snapshot ring (`out/.snapshots/<timestamp>/`), cook re-cook.

## See also

- [`engine-spec.md` §25](../../engine-spec.md) — full design rationale.
