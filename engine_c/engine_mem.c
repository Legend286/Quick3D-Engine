/* SPDX-License-Identifier: MIT */
#include "engine_mem.h"
#include <stdlib.h>
#include <string.h>
#include <stdatomic.h>

#if defined(__APPLE__) || defined(__linux__)
#include <sys/mman.h>
#elif defined(_WIN32)
#include <windows.h>
#endif

// --- General Allocations ---

void* engine_alloc(size_t size) {
    if (size == 0) return NULL;
    return malloc(size);
}

void* engine_alloc_zeroed(size_t size) {
    if (size == 0) return NULL;
    return calloc(1, size);
}

void engine_free(void* ptr) {
    free(ptr);
}

// --- Reference Counting ---

typedef struct {
    _Atomic(int32_t) ref_count;
    engine_dtor_fn dtor;
    // Align the payload to 16 bytes (standard SIMD/general alignment)
    uint8_t padding[16 - (sizeof(_Atomic(int32_t)) + sizeof(engine_dtor_fn))];
} engine_ref_header_t;

void* engine_ref_alloc(size_t size, engine_dtor_fn dtor) {
    if (size == 0) return NULL;
    
    size_t total_size = sizeof(engine_ref_header_t) + size;
    engine_ref_header_t* header = (engine_ref_header_t*)malloc(total_size);
    if (!header) return NULL;

    atomic_init(&header->ref_count, 1);
    header->dtor = dtor;

    return (void*)(header + 1);
}

void engine_ref_retain(void* obj) {
    if (!obj) return;
    engine_ref_header_t* header = ((engine_ref_header_t*)obj) - 1;
    atomic_fetch_add_explicit(&header->ref_count, 1, memory_order_relaxed);
}

void engine_ref_release(void* obj) {
    if (!obj) return;
    engine_ref_header_t* header = ((engine_ref_header_t*)obj) - 1;
    
    // decrement returns the PREVIOUS value
    if (atomic_fetch_sub_explicit(&header->ref_count, 1, memory_order_acq_rel) == 1) {
        if (header->dtor) {
            header->dtor(obj);
        }
        free(header);
    }
}

// --- Bump Allocator ---

typedef struct engine_bump_block {
    struct engine_bump_block* next;
    size_t capacity;
    size_t offset;
    uint8_t data[]; // flexible array member
} engine_bump_block_t;

struct engine_bump_allocator {
    engine_bump_block_t* head;
    engine_bump_block_t* current;
    size_t default_capacity;
};

static engine_bump_block_t* allocate_block(size_t capacity) {
    engine_bump_block_t* block = (engine_bump_block_t*)malloc(sizeof(engine_bump_block_t) + capacity);
    if (!block) return NULL;
    block->next = NULL;
    block->capacity = capacity;
    block->offset = 0;
    return block;
}

engine_bump_allocator_t* engine_bump_create(size_t initial_capacity) {
    if (initial_capacity == 0) initial_capacity = 64 * 1024; // 64KB default
    engine_bump_allocator_t* alloc = (engine_bump_allocator_t*)malloc(sizeof(engine_bump_allocator_t));
    if (!alloc) return NULL;
    
    alloc->head = allocate_block(initial_capacity);
    alloc->current = alloc->head;
    alloc->default_capacity = initial_capacity;
    
    return alloc;
}

void engine_bump_destroy(engine_bump_allocator_t* alloc) {
    if (!alloc) return;
    engine_bump_block_t* block = alloc->head;
    while (block) {
        engine_bump_block_t* next = block->next;
        free(block);
        block = next;
    }
    free(alloc);
}

void* engine_bump_alloc(engine_bump_allocator_t* alloc, size_t size, size_t align) {
    if (!alloc || size == 0) return NULL;
    if (align == 0) align = 8;
    
    // Ensure alignment is power of two
    if ((align & (align - 1)) != 0) return NULL;

    engine_bump_block_t* block = alloc->current;
    
    // Compute aligned offset
    size_t aligned_offset = (block->offset + align - 1) & ~(align - 1);
    
    if (aligned_offset + size <= block->capacity) {
        block->offset = aligned_offset + size;
        return block->data + aligned_offset;
    }
    
    // Fast path failed, look in subsequent blocks if they exist (from previous resets)
    engine_bump_block_t* search = block->next;
    while (search) {
        size_t s_aligned_offset = (search->offset + align - 1) & ~(align - 1);
        if (s_aligned_offset + size <= search->capacity) {
            search->offset = s_aligned_offset + size;
            alloc->current = search;
            return search->data + s_aligned_offset;
        }
        search = search->next;
    }
    
    // Need a new block
    size_t req_capacity = alloc->default_capacity;
    if (size > req_capacity) {
        req_capacity = size; // Allocate a large block if requested size is huge
    }
    
    engine_bump_block_t* new_block = allocate_block(req_capacity);
    if (!new_block) return NULL;
    
    size_t new_aligned_offset = (new_block->offset + align - 1) & ~(align - 1);
    new_block->offset = new_aligned_offset + size;
    
    // Append to end of chain
    engine_bump_block_t* tail = alloc->current;
    while (tail->next) {
        tail = tail->next;
    }
    tail->next = new_block;
    alloc->current = new_block;
    
    return new_block->data + new_aligned_offset;
}

void engine_bump_reset(engine_bump_allocator_t* alloc) {
    if (!alloc) return;
    engine_bump_block_t* block = alloc->head;
    while (block) {
        block->offset = 0;
        block = block->next;
    }
    alloc->current = alloc->head;
}
