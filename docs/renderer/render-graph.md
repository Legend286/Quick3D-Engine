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
- `IGpuWorkTimingSource`: Reports frame-indexed submitted work counts so
  delayed GPU timestamps train the correct scheduler domain without readback.
- `RendererPluginContext.EnableGlobalExtensions`: Allows the interactive
  renderer to consume process-wide extension resources while keeping
  device-isolated offscreen renderers disconnected from them.

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

Each completed capture is associated with its exact compiled plan, execution
frame, queue, and successfully recorded pass slots. Graphics and asynchronous
compute results are merged only when their execution frame matches. Publishing
a capture replaces the complete prior per-pass vector, including missing
samples, so invalid, unsupported, and queue-local gaps cannot inherit another
pass's previous timing. Captures from a rebuilt plan are discarded even when
the new plan happens to contain the same number of passes.

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
budgets. The renderer begins the scheduler frame before executing any graph
pass, so extension ordering and absent shadow passes cannot leave stale
admissions or reset diagnostics after earlier work. Unused directional and
punctual time becomes bounded carry-over for a
later burst, while completed asynchronous GPU timings update estimated unit
cost with an exponential moving average. Transform and camera invalidation are
correctness work: forced admission bypasses time and unit limits so all
affected shadows use matching transforms in the current frame.

DDGI uses a separate measured 4 ms domain. It begins at a 32-probe estimate and
adjusts submission size from 1 through 128 probes using delayed pass timings.
Lighting revisions and initial scene bakes change GPU scheduling priority but
never bypass admission, so scene import and animated lights cannot turn probe
convergence into an unbounded frame spike.
The DDGI scheduler combines current clipmap requests with a rotating 8,192-slot
persistent-atlas scan. Consequently, a light edit reaches built probes outside
the current clipmaps without scanning all 262,144 slots in one frame. Empty
sky-only probes are eligible only when the independent sky revision changes.

Directional shadows use a 2 ms base target and can consume accumulated carry
for up to four cascades. Dirty cascades share one culling encoder and disjoint
indirect-command ranges before their four persistent pages are rendered.
Punctual shadows use a separate 6 ms base domain, homogeneous batches of at
most 24 faces, and a 96-face carry burst ceiling. Static and movable updates
for one light remain atomic so sampling matrices cannot lead rendered pages.
Forced invalidation can exceed the normal two-batch working set when a graph
rebuild dirties many lights together. Per-batch culling and indirect-command
buffers therefore grow on the render thread to match the admitted point and
spot batches, remain distinct until submission completes, and are retained for
reuse by later frames.

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

Renderer extension post-passes execute after scene overlays and before ImGui.
This lets diagnostics such as DDGI probe markers composite over the rendered
scene without painting over editor controls.

Pass display names describe the active implementation. Raster scene passes are
reported as `Forward PBR`; compute path-traced scene passes are reported as
`Path Tracing`, independent of legacy names stored in scene JSON.
