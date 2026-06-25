# Metal Backend

> **TODO(rhi):** Implementation notes for the Metal backend. Mesh-shader, persistent device, immutable resources, triple-buffered ring buffers.

The Metal backend targets Apple Silicon baseline (Metal 3). Mesh-shader primitive is required for the Forward+ culling kernel.

See [`engine-spec.md` §21 - risks](../../engine-spec.md) for the Metal-only assumptions guidance.
