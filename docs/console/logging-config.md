# Logging Configuration

The logger reads its runtime config from `.eeproj/modules.json#logging` at engine startup. The mode is a single int (0..4) that maps to `EngineLogLevel` filter + the FATAL crash-dump behaviour.

## Mode mapping (0..4)

| Mode | Name | Level filter (drops anything below) |
| --- | --- | --- |
| 0 | errors-only | ERROR + FATAL |
| 1 | errors+warnings | WARN + ERROR + FATAL |
| 2 | info (default) | INFO + WARN + ERROR + FATAL |
| 3 | debug | DEBUG + down |
| 4 | verbose / trace | TRACE + down |

Default when key missing: mode 2 (INFO).

## Schema

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

## Translation to `engine_log_init`

The C# loader reads this block at engine startup:

1. Resolve file paths — `$(project_root)/.eeproj/${path}` for both `crash_dump_path` and `rolling_file_path`.
2. Translate `log_mode` to `EngineLogConfig.global_level` per the table above.
3. For each entry in `module_overrides`, translate the int to a level and call `engine_log_set_module_level("<module>", level)` after init.
4. Pass the resolved paths and capacities into `engine_log_init(&config)`.

## Module name set

`module_overrides` keys must be one of the engine scope names from [`AGENTS.md` §2.3](../AGENTS.md) (the engine scope taxonomy) or other short lowercase strings matching `^[a-z0-9_-]{1,63}$`. Unknown module names are added dynamically at runtime via a future cook hook.

## Console host contract (mac + windows)

Per the user's confirmation: **no system terminal auto-spawn**. The Avalonia dock console panel inside the editor is the only log surface on macOS. The engine always writes to stdout + a rolling file so CLI/CI tools can pick the log up. Windows: also `AllocConsole` is supported but optional; the dock panel is canonical.

## See also

- [`engine-spec.md` §20.1](../../engine-spec.md) — Phase 0 brief.
- [`docs/architecture/engine_log.md`](../architecture/engine_log.md) — runtime API surface.
- [`AGENTS.md` §5](../AGENTS.md) — file integrity machinery (atomic file writes, crash dump format).
