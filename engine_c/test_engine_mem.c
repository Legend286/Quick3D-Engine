/* SPDX-License-Identifier: MIT */
#include "engine_mem.h"
#include <stdio.h>
#include <assert.h>
#include <string.h>
#include <stdbool.h>

static bool dtor_called = false;

static void my_dtor(void* obj) {
    dtor_called = true;
}

void test_ref_allocator() {
    printf("Running test_ref_allocator...\n");
    dtor_called = false;
    
    // Allocate integer with ref count
    int* val = (int*)engine_ref_alloc(sizeof(int), my_dtor);
    assert(val != NULL);
    *val = 42;
    
    // Retain it (ref count = 2)
    engine_ref_retain(val);
    
    // Release it (ref count = 1)
    engine_ref_release(val);
    assert(!dtor_called);
    
    // Release it (ref count = 0)
    engine_ref_release(val);
    assert(dtor_called);
    
    printf("test_ref_allocator passed.\n");
}

void test_bump_allocator() {
    printf("Running test_bump_allocator...\n");
    
    // Create allocator with small capacity to test chaining
    engine_bump_allocator_t* alloc = engine_bump_create(64);
    assert(alloc != NULL);
    
    // Allocate 32 bytes (fits in block 1)
    void* ptr1 = engine_bump_alloc(alloc, 32, 8);
    assert(ptr1 != NULL);
    
    // Allocate 48 bytes (forces new block creation)
    void* ptr2 = engine_bump_alloc(alloc, 48, 8);
    assert(ptr2 != NULL);
    assert(ptr1 != ptr2); // They must not overlap
    
    // Reset allocator
    engine_bump_reset(alloc);
    
    // Allocate 40 bytes (should fit in the FIRST block again, since it was reset)
    void* ptr3 = engine_bump_alloc(alloc, 40, 8);
    assert(ptr3 != NULL);
    assert(ptr3 == ptr1); // It should reuse the exact same memory!
    
    // Allocate 60 bytes (should fit in the SECOND block because first block is full)
    void* ptr4 = engine_bump_alloc(alloc, 60, 8);
    assert(ptr4 != NULL);
    assert(ptr4 == ptr2); // It should reuse the second block's memory!
    
    engine_bump_destroy(alloc);
    
    printf("test_bump_allocator passed.\n");
}

int main() {
    test_ref_allocator();
    test_bump_allocator();
    printf("All memory tests passed successfully!\n");
    return 0;
}
