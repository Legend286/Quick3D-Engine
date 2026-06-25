# `engine_c/`

C/C++ engine core. Owns:

- The stable public C ABI (`engine.h`).
- RHI (`rhi/`), Metal backend, Vulkan stub.
- Allocators (`mem/`).
- Log (`engine_log.h`, `engine_log.c`).
- ECS C-export wrapper (`ecs/`).
- Physics wrapper, audio wrapper.
- Vendor deps via CMake `FetchContent` (under `third_party/`).
