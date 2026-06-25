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

## `modules.json#logging` shape

```json
{
  "logging": {
    "log_mode": 2,
    "module_overrides": {
      "physics": 3,
      "audio":   4,
      "render":  2,
      "cook":    1
    },
    "enable_crash_dump": true,
    "crash_dump_path":   "out/logs/crash.json",
    "rolling_file_path": "out/logs/engine.log",
    "rolling_max_size_mb": 5,
    "ring_capacity_records": 1024,
    "max_msg_bytes": 512
  }
}
```

Mode 0..4 maps to ERROR / WARN / INFO / DEBUG / TRACE inside engine_c/engine_log. Default 2 (no key) means INFO. See [`docs/console/logging-config.md`](console/logging-config.md) for the full schema.

## See also

- [`engine-spec.md` §25](../../engine-spec.md) — full design rationale.
- [`docs/console/logging-config.md`](console/logging-config.md) — logging schema + mode mapping.
