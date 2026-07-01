# Memory Allocator (`engine_mem`)

**Purpose**: Provides a bump-pointer and linear memory allocator for the C engine core, avoiding `malloc`/`free` overhead in hot paths. Currently supports basic tracking and arena resets.

## Public API Surface
- `engine_mem_init(size)`: Initializes the global memory state.
- `engine_mem_alloc(size)`: Allocates `size` bytes.
- `engine_mem_free(ptr)`: Stub for potential pooling/freelists.
- `engine_mem_stats()`: Returns current memory usage info.

## Usage Example
```c
engine_mem_init(1024 * 1024 * 64); // 64 MB pool
void* buffer = engine_mem_alloc(256);
```

## Performance
Designed for high-throughput single-threaded allocations or thread-local arenas. Reduces sys-calls during the frame loop.
