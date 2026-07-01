/* SPDX-License-Identifier: MIT */
#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#define ENGINE_API __declspec(dllexport)
#else
#define ENGINE_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Global General-Purpose Allocations
 * Used for long-lived objects and subsystems. Avoid in hot paths.
 */
ENGINE_API void* engine_alloc(size_t size);
ENGINE_API void* engine_alloc_zeroed(size_t size);
ENGINE_API void  engine_free(void* ptr);

/*
 * Reference Counted Allocations
 * Allocates `size` bytes, prefixed with a hidden atomic reference count block.
 * When refcount drops to 0, `dtor` (if provided) is called before the memory is freed.
 */
typedef void (*engine_dtor_fn)(void* obj);

ENGINE_API void* engine_ref_alloc(size_t size, engine_dtor_fn dtor);
ENGINE_API void  engine_ref_retain(void* obj);
ENGINE_API void  engine_ref_release(void* obj);

/*
 * Bump Allocator
 * Tiered memory blocks that grow as needed. Resetting the allocator frees all
 * memory at once (or reuses the blocks). Ideal for subsystem tick logic or frame allocation.
 */
typedef struct engine_bump_allocator engine_bump_allocator_t;

ENGINE_API engine_bump_allocator_t* engine_bump_create(size_t initial_capacity);
ENGINE_API void  engine_bump_destroy(engine_bump_allocator_t* alloc);
ENGINE_API void* engine_bump_alloc(engine_bump_allocator_t* alloc, size_t size, size_t align);
ENGINE_API void  engine_bump_reset(engine_bump_allocator_t* alloc);

#ifdef __cplusplus
}
#endif
