# Thread Model

> **TODO(architecture):** SPSC/MPSC primitive patterns per engine-spec.md §2.2.

Threads:

- **Game Thread** — FLECS progress, gameplay, AI tickers, input.
- **Render Thread** — frame graph, RHI command issuance, GPU upload ring.
- **Physics Thread** — fixed-step Jolt simulation.
- **Asset IO Thread** — glTF/PNG decompression, mesh/texture upload, with editor-side progress UI.
- **ECS Worker Pool** — FLECS job mode dispatcher.

Cross-thread primitives: SPSC ring (G→R), MPSC queue (I→G/R), atomic counters.

See [`engine-spec.md` §2.2](../../engine-spec.md).
