# Logger — `engine_log`

The engine's C-side logging subsystem. Phase 0 deliverable. See the header at [`engine_c/engine_log.h`](../../engine_c/engine_log.h) for the exact API surface.

## Purpose

Capture diagnostic messages from any engine thread (Game, Render, Physics, Asset IO, ECS workers) and deliver them to:

1. **stdout** — for `dotnet run`-style console output.
2. **Rolling file** — for crash forensics.
3. **In-memory ring** — drained by the C# Avalonia console panel.

Plus:

- Severity filtering with per-module overrides.
- FATAL events that flush, dump a diagnostic JSON, and break into the debugger if attached.

## Public API surface

ABI‑stable exports (visible across hot reloads):

| Function | Role |
| --- | --- |
| `engine_log_init` | Bring up the log subsystem. Idempotent per process. |
| `engine_log_shutdown` | Flush sinks + tear down. |
| `engine_log_set_global_level` / `engine_log_global_level_get` | Default severity filter. |
| `engine_log_set_module_level` | Per-subsystem override (e.g. bump `physics` to DEBUG). |
| `engine_log_emit` | Producer entry point (called by macros). |
| `engine_log_drain` | C# UI consumer drains records out of the post-pump ring. |
| `engine_log_sink_register` / `engine_log_sink_unregister` | Plug a custom sink (e.g. a Roslyn REPL stream). |
| `engine_log_flush_blocking` | Block until the pump has drained. |
| `engine_log_dump_diagnostics` | Write `crash_dump_path` JSON. |

Macros for production code: `ENGINE_LOG_TRACE` / `_DEBUG` / `_INFO` / `_WARN` / `_ERROR` / `_FATAL`.

## Threading model

- **Producer side**: lock-free MPSC ring. Each thread writes into its next slot, publishes the producer index atomically. Filtering happens on the producer path (`level <= global_level`) so dropped messages cost one atomic load.
- **Pump**: a single internal thread drains the ring, calls registered sinks in registration order. Sinks must not block.
- **Consumer**: `engine_log_drain` returns records that the pump has already processed. C# treats records as transient pins — copy bytes into a managed buffer immediately.

## Severity ordering

Levels are ordered ascending so the filter check is just `level <= global_level`. New levels can be added without ABI breakage.

## Module-scoped overrides

```c
engine_log_set_module_level("physics",   ENGINE_LOG_DEBUG);
engine_log_set_module_level("audio",     ENGINE_LOG_TRACE);
```

Per-module overrides are stored in a lock-protected map owned by the engine. The map survives the ALC swap because it lives in the C ABI, not the C# heap.

## FATAL semantics

`ENGINE_LOG_FATAL` flushes sinks, writes `crash_dump_path`, then:

- Tries `__builtin_debugtrap` (clang/gcc).
- Falls back to `__debugbreak` (MSVC).
- Falls back to `abort()`.

Never returns. Used for "this cannot happen" conditions and unrecoverable errors.

## Lifetime contract for `EngineLogRecord`

Records handed back from `engine_log_drain` carry `const char*` pointers into the ring slot's internal storage. They are valid until the **next** `engine_log_emit` that wraps the same slot. C# must copy out before that wrap, otherwise stale-pointer reads will occur.

For C# consumers:

```csharp
foreach (EngineLogRecord rec in EngineLog.DrainRecords(max))
{
    var msg = rec.MsgUtf8;   // copies the bytes into a managed string
    // rec.Msg is no longer safe to use once the engine advances
}
```

## Per-feature doc rules

This doc exists because `engine_log.h` is the first public engine C ABI exported surface. Future additions to the log API update this doc in the same commit per `AGENTS.md §3.4`.

## See also

- [`engine-spec.md` §20.1](../../engine-spec.md) — original Phase 0 brief.
- [`AGENTS.md` §4](../AGENTS.md) — comment policy that constrains the header itself.
