/* SPDX-License-Identifier: MIT */
#ifndef ENGINE_LOG_H
#define ENGINE_LOG_H

/** Engine log subsystem C ABI. See docs/architecture/engine_log.md. */

#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>

#  if defined(_MSC_VER)
#    include <intrin.h>
#  endif

#ifdef __cplusplus
extern "C" {
#endif

#ifndef ENGINE_API
#  ifdef _WIN32
#    define ENGINE_API __declspec(dllimport)
#  else
#    define ENGINE_API __attribute__((visibility("default")))
#  endif
#endif

/**
 * Severity levels, ordered ascending. Producer filter is `level <= effective_level`
 * where `effective_level` is the per-module override or, if none, the global level.
 * Adding a new level is binary-compatible; consumers ignore unknown values.
 */
typedef enum EngineLogLevel {
    ENGINE_LOG_OFF   = 0,
    ENGINE_LOG_FATAL = 1,
    ENGINE_LOG_ERROR = 2,
    ENGINE_LOG_WARN  = 3,
    ENGINE_LOG_INFO  = 4,
    ENGINE_LOG_DEBUG = 5,
    ENGINE_LOG_TRACE = 6
} EngineLogLevel;

/**
 * One log record handed out via engine_log_drain().
 *
 * Lifetime contract:
 *   - `file` and `msg` point into fixed-size ring slots inside the log system.
 *   - They are valid until the next call to engine_log_emit() that wraps
 *     the same slot index.
 *   - Consumers MUST copy out before that wrap, typically immediately on drain.
 *
 * Records are POD: no destructor, no allocator, no cross-thread pointers.
 */
typedef struct EngineLogRecord
{
    int64_t     timestamp_ns;
    int32_t     level;
    int32_t     thread_id;
    const char* file;
    int32_t     line;
    const char* msg;
    uint32_t    msg_len;
    const char* module;
} EngineLogRecord;

/**
 * Sink callback. The record pointer is valid only for the duration of the call;
 * sinks MUST copy out anything they want to retain. Sinks run on the internal
 * sink-pump thread and MUST NOT block.
 */
typedef void (*EngineLogSinkFn)(const EngineLogRecord* rec, void* userdata);

/**
 * Custom sink registration. `abi` MUST be set to `sizeof(EngineLogSink)`;
 * if the engine sees a larger value it refuses to register. Fields may only
 * be appended at the end of this struct, never reordered in place.
 */
typedef struct EngineLogSink
{
    uint32_t          abi;
    EngineLogSinkFn   write;
    void*             userdata;
    const char*       name;
} EngineLogSink;

/**
 * Constructor arguments for engine_log_init.
 *
 * `ring_capacity_records`: number of records in the MPSC ring (power of 2).
 * `max_msg_bytes`: per-record message byte cap (includes file/module bytes).
 * `enable_crash_dump`: if non-zero, FATAL emits a crash dump before break.
 */
typedef struct EngineLogConfig
{
    uint32_t    abi;
    int32_t     global_level;
    uint32_t    ring_capacity_records;
    uint32_t    max_msg_bytes;
    int32_t     enable_crash_dump;
    const char* crash_dump_path;
} EngineLogConfig;

/**
 * Initialize the log subsystem. Returns 0 on success, non-zero on config error.
 * Safe to call once per process. The config is copied internally.
 */
ENGINE_API int32_t engine_log_init(const EngineLogConfig* config);

/**
 * Tear down the log subsystem. Flushes all sinks. Safe to call once per process.
 */
ENGINE_API void    engine_log_shutdown(void);

/**
 * Set or get the global severity filter.
 * `module` NULL means the global filter (applied when no per-module override exists).
 * Per-module overrides survive hot-reload so the C# console can keep live tuning.
 */
/**
 * Manage severity filters. `engine_log_set_module_level` deep-copies `module`
 * internally so the caller's buffer can be freed when the call returns. The
 * per-module map lives in the C ABI and survives C# AssemblyLoadContext swap.
 *
 * `engine_log_module_level_get` returns the effective level for `module`
 * (the per-module override if set, otherwise the global level). The producer
 * macros call this on each emit so per-module overrides filter cheaply.
 */
ENGINE_API void    engine_log_set_global_level(int32_t level);
ENGINE_API int32_t engine_log_global_level_get(void);
ENGINE_API void    engine_log_set_module_level(const char* module, int32_t level);
ENGINE_API int32_t engine_log_module_level_get(const char* module);

/**
 * Producer-side emit. Format-once-on-producer: the message bytes must already
 * be UTF-8 encoded. `file` and `module` are pointers to memory that must
 * remain valid for at least the duration of the call (typically string literals).
 *
 * Thread-safe: callable from any engine thread. Lock-free on the producer side.
 *
 * Precondition: `msg_len` MUST be <= `EngineLogConfig.max_msg_bytes` from the
 * most recent init. Pass-through larger values will be truncated; the contract
 * exists so producers can detect mis-sized buffers rather than corrupt slots.
 */
ENGINE_API void    engine_log_emit(int32_t level,
                                   const char* file, int32_t line,
                                   const char* module,
                                   const char* msg, uint32_t msg_len);

/**
 * Producer-side emit. Format-once-on-producer: the message bytes must already
 * be UTF-8 encoded. `file` and `module` are pointers to memory that must
 * remain valid for at least the duration of the call (typically string literals).
 *
 * Thread-safe: callable from any engine thread. Lock-free on the producer side.
 */
/**
 * C# / UI consumer drain. Pops records that have been sink-pumped but not
 * yet observed by this drain call. Returns the number of records written
 * into out_records (0..max_records).
 *
 * Records are sink-stage pins; their `msg` and `file` pointers are valid
 * until the next internal ring slot wrap. C# should copy the bytes into a
 * managed buffer immediately on drain.
 */
ENGINE_API int32_t engine_log_drain(EngineLogRecord* out_records, int32_t max_records);

/**
 * Register or unregister a sink. Sinks run on the internal sink-pump thread.
 * Returns a stable sink id (>= 0) on success, negative on error.
 */
ENGINE_API int32_t engine_log_sink_register(const EngineLogSink* sink);
ENGINE_API void    engine_log_sink_unregister(int32_t sink_id);

/**
 * Block until all sinks have drained the in-pump queue. Use sparingly:
 * primarily at shutdown and at FATAL events.
 */
ENGINE_API void    engine_log_flush_blocking(void);

/**
 * Write a diagnostic dump describing the log subsystem state to
 * EngineLogConfig.crash_dump_path. Called automatically from FATAL.
 */
ENGINE_API void    engine_log_dump_diagnostics(void);

/* Production-side macros. Captures __FILE__ / __LINE__ at the call site.
 * Per-module overrides filter cheaply on the producer path. Stack buffer is
 * 512 bytes; longer messages get truncated to fit (rare; FATAL-shaped events
 * use the FATAL path which does not enforce this). */
#define ENGINE_LOG_IMPL(level, module, fmt, ...) do {                                      \
    if ((level) <= engine_log_module_level_get(module)) {                                  \
        char _engine_log_buf[512];                                                         \
        int _engine_log_n =                                                                \
            snprintf(_engine_log_buf, sizeof(_engine_log_buf), (fmt), ##__VA_ARGS__);      \
        if (_engine_log_n < 0) _engine_log_n = 0;                                          \
        if ((uint32_t)_engine_log_n > sizeof(_engine_log_buf))                             \
            _engine_log_n = (int)sizeof(_engine_log_buf);                                  \
        engine_log_emit((level), __FILE__, __LINE__,                                        \
                        (module), _engine_log_buf, (uint32_t)_engine_log_n);               \
    }                                                                                       \
} while (0)

#define ENGINE_LOG_TRACE(module, fmt, ...) ENGINE_LOG_IMPL(ENGINE_LOG_TRACE, module, fmt, ##__VA_ARGS__)
#define ENGINE_LOG_DEBUG(module, fmt, ...) ENGINE_LOG_IMPL(ENGINE_LOG_DEBUG, module, fmt, ##__VA_ARGS__)
#define ENGINE_LOG_INFO(module, fmt, ...)  ENGINE_LOG_IMPL(ENGINE_LOG_INFO,  module, fmt, ##__VA_ARGS__)
#define ENGINE_LOG_WARN(module, fmt, ...)  ENGINE_LOG_IMPL(ENGINE_LOG_WARN,  module, fmt, ##__VA_ARGS__)
#define ENGINE_LOG_ERROR(module, fmt, ...) ENGINE_LOG_IMPL(ENGINE_LOG_ERROR, module, fmt, ##__VA_ARGS__)

/**
 * Platform-debugrap dispatch. Defined at file scope so the FATAL macro
 * below doesn't have to embed preprocessor directives (which C99/C17
 * forbid inside function-like macro bodies that span lines via backslash).
 * Tries clang/gcc's __builtin_debugtrap, falls back to MSVC __debugbreak,
 * falls back to abort().
 */
#if defined(__has_builtin) && __has_builtin(__builtin_debugtrap)
#  define ENGINE_LOG_DEBUGTRAP() __builtin_debugtrap()
#elif defined(_MSC_VER)
#  define ENGINE_LOG_DEBUGTRAP() __debugbreak()
#else
#  define ENGINE_LOG_DEBUGTRAP() abort()
#endif

/** FATAL emits, flushes sinks synchronously, dumps diagnostics, then traps the
 *  debugger if attached, otherwise aborts. Never returns. */
#define ENGINE_LOG_FATAL(module, fmt, ...) do {                                                \
    char _engine_log_fbuf[512];                                                               \
    int  _engine_log_fn =                                                                     \
        snprintf(_engine_log_fbuf, sizeof(_engine_log_fbuf), (fmt), ##__VA_ARGS__);          \
    if (_engine_log_fn < 0) _engine_log_fn = 0;                                               \
    if ((uint32_t)_engine_log_fn > sizeof(_engine_log_fbuf))                                  \
        _engine_log_fn = (int)sizeof(_engine_log_fbuf);                                       \
    engine_log_emit(ENGINE_LOG_FATAL, __FILE__, __LINE__,                                      \
                    (module), _engine_log_fbuf, (uint32_t)_engine_log_fn);                   \
    engine_log_flush_blocking();                                                              \
    engine_log_dump_diagnostics();                                                            \
    ENGINE_LOG_DEBUGTRAP();                                                                   \
} while (0)

#ifdef __cplusplus
} /* extern "C" */
#endif

#endif /* ENGINE_LOG_H */
