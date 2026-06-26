/* SPDX-License-Identifier: MIT */
#include "engine_log.h"

#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <stdalign.h>
#include <stdatomic.h>
#include <stdio.h>
#include <pthread.h>
#include <time.h>
#include <unistd.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <fcntl.h>
#include <errno.h>

#define ENGINE_LOG_OK                0
#define ENGINE_LOG_ERR_INVALID_ARG  -1
#define ENGINE_LOG_ERR_NOMEM        -2
#define ENGINE_LOG_ERR_ALREADY      -3
#define ENGINE_LOG_ERR_NEVER        -4

#define ENGINE_LOG_DEFAULT_RING_CAP      1024u
#define ENGINE_LOG_DEFAULT_MSG_CAP        512u
#define ENGINE_LOG_CRASH_BUFFER_CAP       32u
#define ENGINE_LOG_ROLLING_MAX_BYTES      (5u * 1024u * 1024u)
#define ENGINE_LOG_DEFAULT_FILE_PATH     "out/logs/engine.log"
#define ENGINE_LOG_DEFAULT_CRASH_PATH    "out/logs/crash.json"
#define ENGINE_LOG_MAX_MODULES            64u
#define ENGINE_LOG_FILE_STR_MAX          256u
#define ENGINE_LOG_MODULE_NAME_MAX        64u

/**
 * Ring slot. The flexible-array `msg[]` carries up to EngineLogState.msg_cap
 * bytes of UTF-8 payload. Producers copy `file` and `module` strings inline
 * so the slot holds stable references even if the caller frees its strings.
 *
 * Sequence number drives the lock-free claim protocol: producers wait until
 * `sequence == producer_idx`, then on publish they set `sequence = producer_idx + 1`.
 * Consumers wait until `slot.sequence == consumed_idx + 1`, then on consume
 * they set `slot.sequence = consumed_idx + cap` so the next producer that wraps
 * this slot can claim it again.
 */
typedef struct EngineLogSlot {
    atomic_int           sequence;
    int64_t              timestamp_ns;
    int32_t              level;
    int32_t              thread_id;
    int32_t              line;
    uint32_t             msg_len;
    char                 file[ENGINE_LOG_FILE_STR_MAX];
    char                 module[ENGINE_LOG_MODULE_NAME_MAX];
    char                 msg[];
} EngineLogSlot;

typedef struct EngineLogSinkEntry {
    EngineLogSink                sink;
    int32_t                      id;
    struct EngineLogSinkEntry*   next;
} EngineLogSinkEntry;

typedef struct EngineLogModuleLevel {
    char        module[ENGINE_LOG_MODULE_NAME_MAX];
    atomic_int  level;
} EngineLogModuleLevel;

/* Crash-buffer record holds string content inline so the JSON dump survives
 * after the originating ring slot has wrapped. Sizes are generous but fixed. */
typedef struct EngineLogCrashRecord {
    int64_t  timestamp_ns;
    int32_t  level;
    int32_t  thread_id;
    int32_t  line;
    char     file[ENGINE_LOG_FILE_STR_MAX];
    char     module[ENGINE_LOG_MODULE_NAME_MAX];
    char     msg[ENGINE_LOG_DEFAULT_MSG_CAP];
} EngineLogCrashRecord;

typedef struct EngineLogState {
    int32_t                initialized;
    atomic_int             global_level;
    EngineLogConfig        config;

    uint32_t               ring_cap;
    uint32_t               ring_mask;
    uint32_t               msg_cap;
    EngineLogSlot*         ring_slots;
    uint8_t*               ring_mem;        /* single allocation backing slots */

    alignas(64) atomic_ullong producer_idx;
    alignas(64) atomic_ullong consumed_idx;

    pthread_mutex_t        sinks_lock;
    EngineLogSinkEntry*    sinks_head;
    int32_t                 next_sink_id;

    pthread_t              pump_thread;
    atomic_int              pump_running;
    atomic_int              pump_should_stop;

    /* Per-module levels are read lock-free by the producer path.
     * Each entry holds an atomic_int so writes from the C# console side
     * and reads from any thread coexist without a producer-side mutex. */
    EngineLogModuleLevel   modules[ENGINE_LOG_MAX_MODULES];
    uint32_t                module_count;

    pthread_mutex_t        crash_lock;
    /* Crash storage uses inline string buffers. Pointers in EngineLogRecord
     * would dangle once their owning ring slot wraps; the dump has to be
     * self-contained to be useful. */
    EngineLogCrashRecord    crash_buffer[ENGINE_LOG_CRASH_BUFFER_CAP];
    uint32_t                crash_buffer_count;
    uint32_t                crash_head;

    /* Default sinks state - referenced only by the in-memory ring sink. */
    pthread_mutex_t        ui_ring_lock;
    EngineLogRecord        ui_ring[ENGINE_LOG_DEFAULT_RING_CAP];
    uint32_t                ui_ring_head;     /* write position */
    uint32_t                ui_ring_count;    /* 0..ring_cap */

    pthread_mutex_t        file_lock;
    FILE*                   file_handle;
    uint64_t                file_bytes;
} EngineLogState;

static EngineLogState g_state;

static int64_t engine_log_now_ns(void) {
    struct timespec ts;
    clock_gettime(CLOCK_REALTIME, &ts);
    return (int64_t)ts.tv_sec * 1000000000LL + (int64_t)ts.tv_nsec;
}

static int32_t engine_log_thread_id(void) {
    return (int32_t)(intptr_t)(void*)pthread_self();
}

static const char* engine_log_level_name(int32_t level) {
    switch (level) {
        case ENGINE_LOG_FATAL: return "FATAL";
        case ENGINE_LOG_ERROR: return "ERROR";
        case ENGINE_LOG_WARN:  return "WARN ";
        case ENGINE_LOG_INFO:  return "INFO ";
        case ENGINE_LOG_DEBUG: return "DEBUG";
        case ENGINE_LOG_TRACE: return "TRACE";
        case ENGINE_LOG_OFF:   return "OFF  ";
        default:               return "?    ";
    }
}

static uint32_t engine_log_next_pow2(uint32_t n) {
    if (n < 2) return 2;
    if ((n & (n - 1)) == 0) return n;
    uint32_t p = 1;
    while (p < n) p <<= 1;
    return p;
}

static const char* engine_log_strnchr(const char* s, char c, uint32_t n) {
    for (uint32_t i = 0; i < n; ++i) {
        if (s[i] == '\0') return NULL;
        if (s[i] == c)   return s + i;
    }
    return NULL;
}

/* ---- Slot claim protocol (Vyukov MPMC pattern, used MPSC here). ---- */

static int engine_log_slot_publish(EngineLogSlot* slot, const EngineLogRecord* rec) {
    /* Truncate file path to a basename-like tail so log lines stay compact. */
    const char* slash = engine_log_strnchr(rec->file, '/', ENGINE_LOG_FILE_STR_MAX - 1);
    const char* file_tail = slash ? slash + 1 : rec->file;

    slot->timestamp_ns = rec->timestamp_ns;
    slot->level        = rec->level;
    slot->thread_id    = rec->thread_id;
    slot->line         = rec->line;
    slot->msg_len      = rec->msg_len;

    strncpy(slot->file, file_tail, sizeof(slot->file) - 1);
    slot->file[sizeof(slot->file) - 1] = '\0';

    if (rec->module) {
        strncpy(slot->module, rec->module, sizeof(slot->module) - 1);
        slot->module[sizeof(slot->module) - 1] = '\0';
    } else {
        slot->module[0] = '\0';
    }

    uint32_t copy_len = rec->msg_len;
    if (copy_len >= g_state.msg_cap) copy_len = g_state.msg_cap - 1;
    memcpy(slot->msg, rec->msg, copy_len);
    slot->msg[copy_len] = '\0';
    slot->msg_len = copy_len;
    return 0;
}

static EngineLogSlot* engine_log_claim_producer_slot(EngineLogSlot** out_slot, uint64_t* out_pos) {
    uint64_t pos = atomic_load_explicit(&g_state.producer_idx, memory_order_relaxed);
    for (;;) {
        EngineLogSlot* slot = &g_state.ring_slots[pos & g_state.ring_mask];
        int32_t seq = atomic_load_explicit(&slot->sequence, memory_order_acquire);
        int64_t diff = (int64_t)seq - (int64_t)pos;
        if (diff == 0) {
            if (atomic_compare_exchange_weak_explicit(
                    &g_state.producer_idx, &pos, pos + 1,
                    memory_order_relaxed, memory_order_relaxed)) {
                *out_slot = slot;
                *out_pos = pos;
                return slot;
            }
        } else if (diff < 0) {
            return NULL;        /* full: caller busy-spins later */
        } else {
            pos = atomic_load_explicit(&g_state.producer_idx, memory_order_relaxed);
        }
    }
}

static void engine_log_publish_slot(EngineLogSlot* slot, uint64_t pos, const EngineLogRecord* rec) {
    engine_log_slot_publish(slot, rec);
    atomic_store_explicit(&slot->sequence, (int32_t)(pos + 1), memory_order_release);
}

/* ---- Slot consume protocol on the pump thread. ---- */

static int engine_log_consume_next(EngineLogSlot** out_slot, uint64_t* out_pos) {
    uint64_t prod = atomic_load_explicit(&g_state.producer_idx, memory_order_acquire);
    uint64_t cons = atomic_load_explicit(&g_state.consumed_idx, memory_order_relaxed);
    if (prod == cons) return 0;

    EngineLogSlot* slot = &g_state.ring_slots[cons & g_state.ring_mask];
    int32_t seq = atomic_load_explicit(&slot->sequence, memory_order_acquire);
    if ((uint64_t)seq != cons + 1) return 0;

    *out_slot = slot;
    *out_pos = cons;
    atomic_store_explicit(&g_state.consumed_idx, cons + 1, memory_order_relaxed);
    return 1;
}

static void engine_log_release_slot(uint64_t pos) {
    EngineLogSlot* slot = &g_state.ring_slots[pos & g_state.ring_mask];
    atomic_store_explicit(&slot->sequence, (int32_t)(pos + g_state.ring_cap), memory_order_release);
}

/* ---- Crash buffer for FATAL dump. ---- */

static void engine_log_crash_push(const EngineLogRecord* rec) {
    pthread_mutex_lock(&g_state.crash_lock);
    uint32_t idx = g_state.crash_head;
    EngineLogCrashRecord* dst = &g_state.crash_buffer[idx];
    dst->timestamp_ns = rec->timestamp_ns;
    dst->level        = rec->level;
    dst->thread_id    = rec->thread_id;
    dst->line         = rec->line;
    if (rec->file) {
        strncpy(dst->file, rec->file, sizeof(dst->file) - 1);
        dst->file[sizeof(dst->file) - 1] = '\0';
    } else {
        dst->file[0] = '\0';
    }
    if (rec->module) {
        strncpy(dst->module, rec->module, sizeof(dst->module) - 1);
        dst->module[sizeof(dst->module) - 1] = '\0';
    } else {
        dst->module[0] = '\0';
    }
    uint32_t n = rec->msg_len;
    if (n >= sizeof(dst->msg)) n = sizeof(dst->msg) - 1;
    if (rec->msg) memcpy(dst->msg, rec->msg, n);
    dst->msg[n] = '\0';
    g_state.crash_head = (idx + 1) % ENGINE_LOG_CRASH_BUFFER_CAP;
    if (g_state.crash_buffer_count < ENGINE_LOG_CRASH_BUFFER_CAP) ++g_state.crash_buffer_count;
    pthread_mutex_unlock(&g_state.crash_lock);
}

/* ---- Default sinks: stdout, rolling file, in-memory ring. ---- */

static void engine_log_sink_stdout(const EngineLogRecord* rec, void* userdata) {
    (void)userdata;
    fprintf(stdout, "%012lld.%03ld %s %5d %-12s %s:%d ",
            (long long)(rec->timestamp_ns / 1000000LL),
            (long)((rec->timestamp_ns / 1000000LL) % 1000LL),
            engine_log_level_name(rec->level),
            rec->thread_id,
            rec->module ? rec->module : "",
            rec->file ? rec->file : "",
            rec->line);
    fwrite(rec->msg, 1, rec->msg_len, stdout);
    fputc('\n', stdout);
    fflush(stdout);
}

static void engine_log_rotate_file_if_needed(void) {
    if (g_state.file_bytes < ENGINE_LOG_ROLLING_MAX_BYTES) return;
    if (g_state.file_handle) {
        fclose(g_state.file_handle);
        g_state.file_handle = NULL;
    }
    char alt[ENGINE_LOG_FILE_STR_MAX * 2];
    const char* cur = ENGINE_LOG_DEFAULT_FILE_PATH;
    snprintf(alt, sizeof(alt), "%s.1", cur);
    /* rename(2) is POSIX atomic — avoids fork+exec hazards of system(). */
    (void)rename(cur, alt);
    g_state.file_handle = fopen(ENGINE_LOG_DEFAULT_FILE_PATH, "a");
    g_state.file_bytes = 0;
}

static void engine_log_sink_file(const EngineLogRecord* rec, void* userdata) {
    (void)userdata;
    pthread_mutex_lock(&g_state.file_lock);
    if (!g_state.file_handle) {
        g_state.file_handle = fopen(ENGINE_LOG_DEFAULT_FILE_PATH, "a");
        g_state.file_bytes = 0;
    }
    if (!g_state.file_handle) {
        pthread_mutex_unlock(&g_state.file_lock);
        return;
    }
    engine_log_rotate_file_if_needed();
    int n = fprintf(g_state.file_handle, "%012lld %s t=%d m=%s f=%s:%d ",
                    (long long)rec->timestamp_ns,
                    engine_log_level_name(rec->level),
                    rec->thread_id,
                    rec->module ? rec->module : "",
                    rec->file ? rec->file : "",
                    rec->line);
    if (n > 0) g_state.file_bytes += (uint64_t)n;
    size_t wrote = fwrite(rec->msg, 1, rec->msg_len, g_state.file_handle);
    g_state.file_bytes += wrote;
    fputc('\n', g_state.file_handle);
    rec->level >= ENGINE_LOG_ERROR ? fflush(g_state.file_handle) : (void)0;
    pthread_mutex_unlock(&g_state.file_lock);
}

static void engine_log_sink_ui_ring(const EngineLogRecord* rec, void* userdata) {
    (void)userdata;
    pthread_mutex_lock(&g_state.ui_ring_lock);
    uint32_t idx = g_state.ui_ring_head;
    
    // Free the old message string if we are wrapping around the ring capacity
    if (g_state.ui_ring_count >= ENGINE_LOG_DEFAULT_RING_CAP) {
        if (g_state.ui_ring[idx].msg) {
            free((void*)g_state.ui_ring[idx].msg);
            g_state.ui_ring[idx].msg = NULL;
        }
    }
    
    g_state.ui_ring[idx] = *rec;
    g_state.ui_ring[idx].msg = rec->msg ? strdup(rec->msg) : NULL;
    
    g_state.ui_ring_head = (idx + 1) % ENGINE_LOG_DEFAULT_RING_CAP;
    if (g_state.ui_ring_count < ENGINE_LOG_DEFAULT_RING_CAP) ++g_state.ui_ring_count;
    pthread_mutex_unlock(&g_state.ui_ring_lock);
}

/* ---- Pump thread loop. ---- */

static void engine_log_fanout(const EngineLogRecord* rec) {
    pthread_mutex_lock(&g_state.sinks_lock);
    for (EngineLogSinkEntry* e = g_state.sinks_head; e; e = e->next) {
        if (e->sink.write) e->sink.write(rec, e->sink.userdata);
    }
    pthread_mutex_unlock(&g_state.sinks_lock);
    if (rec->level >= ENGINE_LOG_ERROR) {
        engine_log_crash_push(rec);
    }
}

static void* engine_log_pump_main(void* arg) {
    (void)arg;
    atomic_store_explicit(&g_state.pump_running, 1, memory_order_release);

    /* Build a transient EngineLogRecord on the stack; pointers reference the slot. */
    EngineLogRecord rec;
    memset(&rec, 0, sizeof(rec));

    while (!atomic_load_explicit(&g_state.pump_should_stop, memory_order_acquire)) {
        EngineLogSlot* slot = NULL;
        uint64_t pos = 0;
        if (engine_log_consume_next(&slot, &pos)) {
            rec.timestamp_ns = slot->timestamp_ns;
            rec.level        = slot->level;
            rec.thread_id    = slot->thread_id;
            rec.line         = slot->line;
            rec.msg_len      = slot->msg_len;
            rec.file         = slot->file;
            rec.module       = *slot->module ? slot->module : NULL;
            rec.msg          = slot->msg;
            engine_log_fanout(&rec);
            engine_log_release_slot(pos);
        } else {
            struct timespec ts = { .tv_sec = 0, .tv_nsec = 100 * 1000 };
            nanosleep(&ts, NULL);
        }
    }
    atomic_store_explicit(&g_state.pump_running, 0, memory_order_release);
    return NULL;
}

/* ---- Per-module level map. ---- */

/* Lock-free module lookup. Caller-supplied module string is typically a static
 * const; we compare against each registered entry with strncmp (module count
 * is bounded so linear scan is fine). Returns the per-module override if
 * found, else the global level. */
static int32_t engine_log_module_lookup(const char* module) {
    int32_t global = atomic_load_explicit(&g_state.global_level, memory_order_relaxed);
    if (!module) return global;
    for (uint32_t i = 0; i < g_state.module_count; ++i) {
        if (strncmp(g_state.modules[i].module, module, ENGINE_LOG_MODULE_NAME_MAX) == 0) {
            return atomic_load_explicit(&g_state.modules[i].level, memory_order_relaxed);
        }
    }
    return global;
}

/* ---- Public API. ---- */

int32_t engine_log_init(const EngineLogConfig* config) {
    if (!config) return ENGINE_LOG_ERR_INVALID_ARG;
    if (g_state.initialized) return ENGINE_LOG_ERR_ALREADY;

    EngineLogConfig cfg = *config;
    if (cfg.abi != sizeof(EngineLogConfig)) return ENGINE_LOG_ERR_INVALID_ARG;
    if (cfg.ring_capacity_records == 0) cfg.ring_capacity_records = ENGINE_LOG_DEFAULT_RING_CAP;
    if (cfg.max_msg_bytes == 0)        cfg.max_msg_bytes        = ENGINE_LOG_DEFAULT_MSG_CAP;
    if (cfg.global_level == 0)         cfg.global_level         = ENGINE_LOG_INFO;

    g_state.ring_cap  = engine_log_next_pow2(cfg.ring_capacity_records);
    g_state.ring_mask = g_state.ring_cap - 1u;
    g_state.msg_cap   = cfg.max_msg_bytes;
    g_state.config    = cfg;

    size_t slot_bytes = sizeof(EngineLogSlot) + (size_t)g_state.msg_cap;
    g_state.ring_mem = (uint8_t*)calloc((size_t)g_state.ring_cap, slot_bytes);
    if (!g_state.ring_mem) return ENGINE_LOG_ERR_NOMEM;
    g_state.ring_slots = (EngineLogSlot*)g_state.ring_mem;
    for (uint32_t i = 0; i < g_state.ring_cap; ++i) {
        atomic_init(&g_state.ring_slots[i].sequence, (int32_t)i);
    }

    atomic_init(&g_state.global_level, cfg.global_level);
    atomic_init(&g_state.producer_idx, 0);
    atomic_init(&g_state.consumed_idx, 0);

    pthread_mutex_init(&g_state.sinks_lock, NULL);
    /* module_lock removed: per-module reads are now lock-free. */
    pthread_mutex_init(&g_state.crash_lock, NULL);
    pthread_mutex_init(&g_state.ui_ring_lock, NULL);
    pthread_mutex_init(&g_state.file_lock, NULL);

    g_state.sinks_head = NULL;
    g_state.next_sink_id = 1;
    g_state.module_count = 0;
    g_state.crash_buffer_count = 0;
    g_state.crash_head = 0;
    g_state.ui_ring_head = 0;
    g_state.ui_ring_count = 0;
    g_state.file_handle = NULL;
    g_state.file_bytes = 0;

    static const EngineLogSink defaults[] = {
        { .abi = sizeof(EngineLogSink), .write = engine_log_sink_stdout,   .userdata = NULL, .name = "stdout" },
        { .abi = sizeof(EngineLogSink), .write = engine_log_sink_file,     .userdata = NULL, .name = "rolling_file" },
        { .abi = sizeof(EngineLogSink), .write = engine_log_sink_ui_ring,  .userdata = NULL, .name = "ui_ring" },
    };
    for (size_t i = 0; i < sizeof(defaults) / sizeof(defaults[0]); ++i) {
        engine_log_sink_register(&defaults[i]);
    }

    atomic_init(&g_state.pump_running, 0);
    atomic_init(&g_state.pump_should_stop, 0);
    if (pthread_create(&g_state.pump_thread, NULL, engine_log_pump_main, NULL) != 0) {
        return ENGINE_LOG_ERR_NOMEM;
    }

    g_state.initialized = 1;
    return ENGINE_LOG_OK;
}

void engine_log_shutdown(void) {
    if (!g_state.initialized) return;
    atomic_store_explicit(&g_state.pump_should_stop, 1, memory_order_release);
    pthread_join(g_state.pump_thread, NULL);

    /* Free ui_ring malloc'd strings. */
    for (uint32_t i = 0; i < g_state.ui_ring_count; ++i) {
        if (g_state.ui_ring[i].msg) {
            free((void*)g_state.ui_ring[i].msg);
            g_state.ui_ring[i].msg = NULL;
        }
    }

    pthread_mutex_lock(&g_state.sinks_lock);
    EngineLogSinkEntry* e = g_state.sinks_head;
    while (e) {
        EngineLogSinkEntry* next = e->next;
        free(e);
        e = next;
    }
    g_state.sinks_head = NULL;
    pthread_mutex_unlock(&g_state.sinks_lock);

    if (g_state.file_handle) {
        fclose(g_state.file_handle);
        g_state.file_handle = NULL;
    }

    if (g_state.ring_mem) {
        free(g_state.ring_mem);
        g_state.ring_mem = NULL;
        g_state.ring_slots = NULL;
    }

    pthread_mutex_destroy(&g_state.sinks_lock);
    /* module_lock removed: locks-free per-module reads. */
    pthread_mutex_destroy(&g_state.crash_lock);
    pthread_mutex_destroy(&g_state.ui_ring_lock);
    pthread_mutex_destroy(&g_state.file_lock);

    g_state.initialized = 0;
}

void engine_log_set_global_level(int32_t level) {
    atomic_store_explicit(&g_state.global_level, level, memory_order_release);
}

int32_t engine_log_global_level_get(void) {
    return atomic_load_explicit(&g_state.global_level, memory_order_relaxed);
}

void engine_log_set_module_level(const char* module, int32_t level) {
    if (!module) {
        engine_log_set_global_level(level);
        return;
    }
    /* No mutex on the write path. Reads on the producer path are
     * lock-free (atomic_int loads). */
    for (uint32_t i = 0; i < g_state.module_count; ++i) {
        if (strncmp(g_state.modules[i].module, module, ENGINE_LOG_MODULE_NAME_MAX) == 0) {
            atomic_store_explicit(&g_state.modules[i].level, level, memory_order_release);
            return;
        }
    }
    if (g_state.module_count < ENGINE_LOG_MAX_MODULES) {
        EngineLogModuleLevel* dst = &g_state.modules[g_state.module_count];
        strncpy(dst->module, module, ENGINE_LOG_MODULE_NAME_MAX - 1);
        dst->module[ENGINE_LOG_MODULE_NAME_MAX - 1] = '\0';
        atomic_init(&dst->level, level);
        ++g_state.module_count;
    }
}

int32_t engine_log_module_level_get(const char* module) {
    return engine_log_module_lookup(module);
}

/* Producer macro boils down to engines_log_emit. The macro itself lives in the header. */
void engine_log_emit(int32_t level,
                     const char* file, int32_t line,
                     const char* module,
                     const char* msg, uint32_t msg_len) {
    if (!g_state.initialized) return;
    if (level > ENGINE_LOG_TRACE) return;
    if (level > atomic_load_explicit(&g_state.global_level, memory_order_relaxed)) return;

    /* Per-module filter - cheap linear scan done in the producer. */
    /* (Module override semantics: if level > module_level_get(), drop.) */
    int32_t mod_level = engine_log_module_lookup(module);
    if (level > mod_level) return;
    if (!msg || msg_len == 0) {
        msg = "";
        msg_len = 0;
    }
    if (msg_len > g_state.msg_cap) msg_len = g_state.msg_cap - 1;

    /* Tight busy-wait. Producers retry at most 64 spins before yielding. */
    EngineLogSlot* slot = NULL;
    uint64_t pos = 0;
    int spins = 0;
    while (engine_log_claim_producer_slot(&slot, &pos) == NULL) {
        if (++spins > 64) {
            struct timespec ts = { .tv_sec = 0, .tv_nsec = 1000 };
            nanosleep(&ts, NULL);
            spins = 0;
        }
    }

    EngineLogRecord rec = {
        .timestamp_ns = engine_log_now_ns(),
        .level        = level,
        .thread_id    = engine_log_thread_id(),
        .line         = line,
        .file         = file ? file : "",
        .module       = module,
        .msg          = msg,
        .msg_len      = msg_len,
    };
    engine_log_publish_slot(slot, pos, &rec);

    /* FAST PATH: FATAL must trigger immediate fan-out so the file sink
     * fsyncs before the engine terminates. The FATAL macro itself calls
     * engine_log_flush_blocking + engine_log_dump_diagnostics + debugtrap
     * after engine_log_emit returns, so we don't duplicate here. */
    if (level == ENGINE_LOG_FATAL) {
        engine_log_fanout(&rec);
    }
}

int32_t engine_log_drain(EngineLogRecord* out_records, int32_t max_records) {
    if (!out_records || max_records <= 0) return 0;
    pthread_mutex_lock(&g_state.ui_ring_lock);
    int32_t drained = 0;
    while (drained < max_records && g_state.ui_ring_count > 0) {
        /* Drain oldest-first. Find oldest = head - count. */
        uint32_t oldest = (g_state.ui_ring_head + ENGINE_LOG_DEFAULT_RING_CAP - g_state.ui_ring_count) % ENGINE_LOG_DEFAULT_RING_CAP;
        out_records[drained] = g_state.ui_ring[oldest];
        --g_state.ui_ring_count;
        ++drained;
    }
    pthread_mutex_unlock(&g_state.ui_ring_lock);
    return drained;
}

void engine_log_free_record(EngineLogRecord* rec) {
    if (rec && rec->msg) {
        free((void*)rec->msg);
        rec->msg = NULL;
    }
}

int32_t engine_log_sink_register(const EngineLogSink* sink) {
    if (!sink) return -1;
    if (sink->abi != sizeof(EngineLogSink)) return -2;
    if (!sink->write) return -3;
    EngineLogSinkEntry* entry = (EngineLogSinkEntry*)calloc(1, sizeof(*entry));
    if (!entry) return -4;
    entry->sink = *sink;
    pthread_mutex_lock(&g_state.sinks_lock);
    entry->id = g_state.next_sink_id++;
    /* Append to head - sink order is not stable but iteration is LIFO of registrations. */
    entry->next = g_state.sinks_head;
    g_state.sinks_head = entry;
    pthread_mutex_unlock(&g_state.sinks_lock);
    return entry->id;
}

void engine_log_sink_unregister(int32_t sink_id) {
    pthread_mutex_lock(&g_state.sinks_lock);
    EngineLogSinkEntry** link = &g_state.sinks_head;
    while (*link) {
        if ((*link)->id == sink_id) {
            EngineLogSinkEntry* dead = *link;
            *link = dead->next;
            free(dead);
            break;
        }
        link = &(*link)->next;
    }
    pthread_mutex_unlock(&g_state.sinks_lock);
}

void engine_log_flush_blocking(void) {
    if (!g_state.initialized) return;
    /* Drain the ring into fanout synchronously. */
    EngineLogSlot* slot = NULL;
    uint64_t pos = 0;
    int spins = 0;
    while (engine_log_consume_next(&slot, &pos)) {
        EngineLogRecord rec = {
            .timestamp_ns = slot->timestamp_ns,
            .level        = slot->level,
            .thread_id    = slot->thread_id,
            .line         = slot->line,
            .msg_len      = slot->msg_len,
            .file         = slot->file,
            .module       = *slot->module ? slot->module : NULL,
            .msg          = slot->msg,
        };
        engine_log_fanout(&rec);
        engine_log_release_slot(pos);
        spins = 0;
    }
    if (g_state.file_handle) fflush(g_state.file_handle);
}

void engine_log_dump_diagnostics(void) {
    const char* path = g_state.config.crash_dump_path
                     ? g_state.config.crash_dump_path
                     : ENGINE_LOG_DEFAULT_CRASH_PATH;
    FILE* out = fopen(path, "w");
    if (!out) return;

    fprintf(out, "{\n");
    fprintf(out, "  \"version\": 1,\n");
    fprintf(out, "  \"engine_abi_version\": 1,\n");
    fprintf(out, "  \"global_level\": %d,\n", engine_log_global_level_get());

    /* Lock-free read of per-module levels. */
    fprintf(out, "  \"module_levels\": [\n");
    for (uint32_t i = 0; i < g_state.module_count; ++i) {
        fprintf(out, "    { \"module\": \"%s\", \"level\": %d }%s\n",
                g_state.modules[i].module,
                atomic_load_explicit(&g_state.modules[i].level, memory_order_relaxed),
                i + 1 < g_state.module_count ? "," : "");
    }
    fprintf(out, "  ],\n");

    pthread_mutex_lock(&g_state.crash_lock);
    fprintf(out, "  \"recent_records\": [\n");
    uint32_t start = (g_state.crash_buffer_count < ENGINE_LOG_CRASH_BUFFER_CAP)
                ? 0
                : g_state.crash_head;
    for (uint32_t i = 0; i < g_state.crash_buffer_count; ++i) {
        uint32_t idx = (start + i) % ENGINE_LOG_CRASH_BUFFER_CAP;
        const EngineLogCrashRecord* r = &g_state.crash_buffer[idx];
        fprintf(out,
                "    { \"ts_ns\": %lld, \"level\": %d, \"tid\": %d, "
                "\"file\": \"%s\", \"line\": %d, "
                "\"module\": \"%s\", \"msg\": \"%s\" }%s\n",
                (long long)r->timestamp_ns, r->level, r->thread_id,
                r->file, r->line,
                r->module, r->msg,
                i + 1 < g_state.crash_buffer_count ? "," : "");
    }
    fprintf(out, "  ],\n");
    pthread_mutex_unlock(&g_state.crash_lock);

    fprintf(out, "  \"ring\": { \"capacity\": %u, \"msg_cap\": %u }\n",
            g_state.ring_cap, g_state.msg_cap);
    fprintf(out, "}\n");
    fflush(out);
    fclose(out);
}
