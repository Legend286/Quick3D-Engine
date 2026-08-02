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
remain renderer-scoped. The executor captures both per-pass CPU command-recording
durations and a render-graph wall-clock duration for each execution. The
per-pass values are diagnostic detail; the frame CPU value includes graph setup,
pass recording, submission, and timing bookkeeping.

GPU timing uses three reusable timestamp-query pools per active queue. Metal
assigns unique counter pairs to each encoder inside a 64-sample logical-pass
block. Explicit draw and dispatch boundaries are preferred; stage-only render
timing sums vertex and fragment intervals. The displayed frame GPU duration is the completed command-buffer GPU start-to-end
span, with graphics and asynchronous-compute spans combined by their longest
queue interval. Per-pass values remain the sampled encoder-boundary durations.
The summed sampled-pass workload is retained separately for scheduling and is
not presented as total frame time. A lower median of recent raw frame spans
feeds the adaptive scheduler. The frame resolves each pool once, and a later
frame polls it without waiting. If all pools are still in flight, the executor
skips that frame's GPU capture rather than blocking rendering. Backends without
timestamp support leave the nullable GPU fields empty.

Each completed capture is associated with its exact compiled plan, execution
frame, queue, and successfully recorded pass slots. Graphics and asynchronous
compute results are merged only when their execution frame matches. Publishing
a capture replaces the complete prior per-pass vector, including missing
samples, so invalid, unsupported, and queue-local gaps cannot inherit another
pass's previous timing. Captures from a rebuilt plan are discarded even when
the new plan happens to contain the same number of passes.

The adaptive work controller uses the lower median of the latest 15 completed
raw GPU frame spans. This rejects isolated frame spikes without hiding sustained
GPU pressure. The diagnostics view reports the current raw span instead of the
median, while sampled pass sums remain available internally as queue-local GPU
work for domain accounting.

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
admissions or reset diagnostics after earlier work. Shadow domains do not bank
idle time into a later burst; completed asynchronous GPU timings update
estimated unit cost with an exponential moving average. Transform and camera
invalidation receive priority but do not bypass admission. Deferred lights
retain their complete committed matrix, origin, and atlas tiles until an atomic
update is admitted.

DDGI uses a separate measured 4 ms domain. It begins at a 32-probe estimate and
adjusts submission size from 1 through 128 probes using delayed pass timings.
Lighting revisions and initial scene bakes change GPU scheduling priority but
never bypass admission, so scene import and animated lights cannot turn probe
convergence into an unbounded frame spike.
The DDGI scheduler combines current clipmap requests with a rotating 8,192-slot
persistent-atlas scan. Consequently, a light edit reaches built probes outside
the current clipmaps without scanning all 262,144 slots in one frame. Empty
sky-only probes are eligible only when the independent sky revision changes.

Directional shadows use a 4 ms base target and admit all four persistent
cascade pages atomically when camera or sun motion dirties their transforms.
The visibility compute pass refreshes shadow push data after those updates,
so the matrices sampled by the main frame always match the pages just encoded.
Punctual shadows use a separate 6 ms base domain and homogeneous batches of at
most 48 faces. Static and movable updates
for one light remain atomic so sampling matrices cannot lead rendered pages.
Moved camera-relevant lights receive a bounded 24-face freshness reserve when
learned timing reduces the ordinary allowance. Per-batch culling and
indirect-command buffers grow on the render thread to match admitted point and
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

## Hybrid Visibility-Primary Path Tracing

When path tracing is active with visibility buffers enabled, the path renderer
reuses the canonical clustered plan's `VisibilityBufferPass` and shared
`RasterSceneGpuCache`. The path plugin contributes only its compute and blit
overlay; it does not construct a `PbrPass`, second rasterizer, or second scene
snapshot. The overlay reads the existing identifier, barycentric, and depth
resources for its primary hit, reconstructs the triangle from the persistent
scene buffers, and retains TLAS queries for secondary transport and shadows.

The same raster visibility resources remain available to `VisibilityPickingPass`,
so entity selection and material drops resolve the picked `PartData` index even
while path tracing is displayed. The raster base pass list owns the shared
resources and remains the sole owner during plan disposal.
