# RHI API

> **TODO(rhi):** Public API surface, mirroring `engine_c/engine.h#rhi_*`.

The RHI is a stable C ABI in `engine_c/engine.h`. Backends (Metal, Vulkan) implement the surface. The C# renderer consumes it via P/Invoke.

## Surface

- Device / Swapchain
- Buffer / Texture / Pipeline / Shader creation
- Command list recording (bind pipeline / set buffer / draw / dispatch)
- Lifecycle (acquire next image, present)

See [`engine-spec.md` §6](../../engine-spec.md).
