# Render Graph

**Purpose**: The Render Graph framework manages high-level GPU execution logic, transient resource aliasing, and render pass dependencies.

## Public API Surface
- `RenderGraphCompiler`: Sorts passes, determines lifetimes, and calculates optimal memory aliasing.
- `RenderGraphExecutor`: Traverses the compiled graph, allocating and executing transient resources efficiently via heap-based memory.
- `RenderPass`: Base class for user-defined rendering passes (e.g., `PbrPass`, `GridPass`).
- `MemoryAliasingPlan`: Represents overlapping resource layouts over time.
- `RenderPassTiming`: CPU command-recording duration and the latest completed
  GPU duration captured for one pass.
- `RenderGraphDiagnosticsSnapshot`: Immutable graph, timing, resource, and
  barrier snapshot exposed through the game-loop hot-reload contract.
  `PlanVersion` changes whenever the renderer replaces its compiled plan.
- `RenderGraphShadowDiagnostics`: Shadow-atlas memory, residency, and
  punctual-light face allocation snapshot.
- `RenderGraphShadowFaceDiagnostics`: Stable page-slot identity and cache
  readiness for one point- or spot-light face.

## Usage Example
```csharp
var compiler = new RenderGraphCompiler();
var plan = compiler.Compile(scene.Passes);

var executor = new RenderGraphExecutor(device);
executor.Execute(plan, sink);
```

## Performance
Leverages `RhiHeap` for memory aliasing. Transient textures are created and destroyed using aliased memory from a central heap, significantly lowering memory footprints and allocation overhead.

Plans with no transient resource declarations do not create a minimum-sized
heap. This is important for persistent offscreen preview plans, which otherwise
would allocate an unused heap for every submitted frame.

The main renderer keeps one `RenderGraphExecutor` alive across frames. Command
recorders remain submission-scoped, while transient heaps and timing history
remain renderer-scoped. The executor captures CPU command-recording duration
for each pass.

GPU timing uses three reusable timestamp-query pools per active queue. Metal
records pass samples at draw and dispatch encoder boundaries. The displayed
graphics workload is the sum of serial graphics pass-marker durations, which
excludes swapchain fences and presentation pacing encoded outside render
work. The frame resolves each pool once, and a later frame polls it without
waiting. If all pools are still in flight, the executor skips that frame's GPU
capture rather than blocking rendering. Backends without timestamp support
leave the nullable GPU fields empty.

Each completed capture replaces the prior per-pass values, including missing
samples. Invalid or unsupported samples therefore return to an unavailable
state instead of leaving a stale timing visible indefinitely.

The displayed frame duration and adaptive work controller use the lower
median of the latest 15 active graphics workloads. This rejects isolated
marker spikes without hiding sustained GPU pressure. The completed command
buffer's GPU start-to-end span remains internal and validates pass samples,
but never controls scheduling because it can include fullscreen display
synchronization.

Skipped passes do not invalidate timing results for passes that recorded valid
encoder-boundary samples. Their duration remains unavailable while valid pass
samples from the same command buffer are reported normally.

Graphs whose compute passes form a prefix are partitioned across compute and
graphics command queues. Compute output resources are collected from declared
writes. The graphics queue renders independent work first, then waits on an RHI
timeline fence immediately before the first consumer of a compute output.
Graphs with a later compute pass fall back to serial graphics execution until
the compiler supports arbitrary multi-batch queue DAGs.

The next compute submission waits for the previous graphics submission before
reusing shared visibility buffers. This reverse timeline edge prevents
cross-frame write/read hazards while preserving overlap between current-frame
compute work and the independent directional-shadow graphics pass.

The renderer GPU work scheduler places cacheable work into per-domain frame
budgets. Directional shadows currently use a 2 ms target and a one-page hard
limit. Dirty cascades are prioritized by validity, target refresh interval,
and maximum staleness. Completed asynchronous GPU timings update the estimated
tile cost using an exponential moving average.

Punctual shadows use a separate 6 ms domain. Static and movable face updates
compete within that domain, while directional updates remain independent.
Transform-dirty faces reserve two units atomically so their static and movable
tiles become visible with one matching sampling matrix.

Diagnostics also derive direct pass dependencies from resource writers,
resource first/last-use lifetimes, access counts, and alias groups from the
compiled plan. This analysis is snapshot-only and does not alter execution.

GPU pass timings use one marker scope per logical render-graph pass. The first
internal Metal encoder records the start counter and the final encoder records
the end counter exactly once. Passes containing several compute or render
encoders therefore retain valid per-pass timings instead of overwriting one
counter slot.

The resource view combines graph declarations with
`GpuResourceRegistry.Capture()`. Graph rows describe virtual lifetimes and
aliasing; allocation rows describe live committed heaps, buffers, textures,
models, shadow pages, and editor preview targets. Heap-backed aliases are not
counted as additional committed memory.

The execution context publishes a monotonically increasing `FrameNumber`.
Frame-shared caches use it to make repeated pass preparation idempotent.
Imported resources can be removed with `RenderGraphExecutor.UnbindTexture`
when a rebuilt plan no longer references them.

Pass display names describe the active implementation. Raster scene passes are
reported as `Forward PBR`; compute path-traced scene passes are reported as
`Path Tracing`, independent of legacy names stored in scene JSON.
