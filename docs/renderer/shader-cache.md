# Shader Compile Cache — Hot-Reload Speedup

## Purpose

`ShaderCompileCache` is the in-process dictionary the renderer uses to
hold compiled `RhiShader` handles alive across scene reloads and
plugin toggles. It lives at
[`engine_cs/Engine.Renderer/Shaders/ShaderCompileCache.cs`](../engine_cs/Engine.Renderer/Shaders/ShaderCompileCache.cs)
and is owned by the renderer (`Renderer.ShaderCompileCache`).

Toggling the `renderer.ddgi` (or any other) plugin rebuilds the
render graph; without a content-equivalent cache the rebuild
recompiles every host shader through the Slang CLI and re-creates
Metal pipeline state from scratch, producing a multi-hundred-millisecond
freeze in the editor UI. The cache makes that freeze vanish: when the
content hash (resolved source bytes + entry + stage + include
directories + CLI argv) hits, the existing `RhiShader` handle is
returned and the Slang/Metal path is skipped.

## Public API surface

| Symbol | Kind | Role |
| --- | --- | --- |
| `Renderer.ShaderCompileCache` | property | Process-wide cache instance owned by the active renderer. |
| `ShaderCompileCache.GetOrCompile(string, Func<string, IDisposable>)` | method | Generation-tagged lookup keyed by the caller's `cacheKey`. Backwards compatible with the original API surface. Note: the factory receives the `cacheKey` so callers can use it inside the closure. |
| `ShaderCompileCache.GetOrCompileHash(source, entry, stage, includeDirs, cliArgs, factory)` | method | **Phase-3 entry point.** SHA256-content-keyed lookup; returns the cached compiled handle without invoking the factory if a content-equivalent entry exists. The factory does not receive the key — see "Known follow-ups" below. |
| `ShaderCompileCache.ComputeContentKey(source, entry, stage, includeDirs, cliArgs)` | static | Stable SHA256 over the same inputs as the lookup; useful for tests/logging. |
| `ShaderCompileCache.BumpGeneration()` / `EvictOlderThan(maxAge)` | methods | Drives the generation counter; eviction sweeps both dictionaries (string-keyed + content-keyed). |
| `ShaderCompileCache.Dispose()` | method | Frees every cached handle. Renderer.Dispose calls this. |

The cache stores `IDisposable` rather than the concrete `RhiShader`
type so the type itself stays unit-testable without a native ABI.
The dictionary layer holds two parallel maps:

1. *Caller-supplied key* — the original API; plugins can feed in
   their own `cacheKey` strings if they want to control eviction
   by content-path + feature-set hash.
2. *Content hash* — phase-3 speedup layer; the cache computes a
   SHA256 digest of the inputs and looks up automatically.

## Usage example

Plugins and passes don't have to do anything special — the
`ClusteredRendererPlugin` already threads `context.Renderer.ShaderCompileCache`
into every `PbrPass` ctor. A custom pass can wire itself similarly:

```csharp
var vs = (RhiShader)renderer.ShaderCompileCache.GetOrCompileHash(
    source: src,
    entry: "vertexMain",
    stage: RhiNative.ShaderStage.Vertex,
    includeDirs: context.ShaderIncludeDirs,
    cliArgs: context.ShaderCliArgs,
    factory: () => RhiShader.FromSource(
        renderer.Device, src, "vertexMain",
        RhiNative.ShaderStage.Vertex, dirs, args));
```

Toggle the `renderer.ddgi` plugin in the editor's
`Tools → Plugins` dialog and observe the rebuild cost collapses
from O(N_compiles × Slang_ms) to O(N_compiles × sha256_lookup).

## Performance characteristics

The cache's lookup path is a single `Dictionary<string, IDisposable>`
read under a lock; the heavy work (Slang parse + Metal pipeline
creation) stays in the factory closure and only runs on
**content** misses, not on cache-key collisions. The wall-clock
savings scale with N shaders that plugin toggles reuse.

The numbers below are *estimator / order-of-magnitude* figures
rather than measured benchmarks — they trade against host-machine
Slang compile latency and Metal pipeline-state depth. Use
`Renderer.GetRenderGraphDiagnostics` to capture concrete numbers
during a toggle for your own workload.

| Path | Without cache (est.) | With cache (est.) |
| --- | --- | --- |
| Plugin toggle (DDGI) | ~700 ms | ~70 ms |
| Scene reload (same scene) | ~700 ms | ~70 ms |
| Scene reload (content changed) | ~700 ms | ~700 ms (cache miss per changed source) |
| Cold process start to first frame | ~700 ms | ~700 ms (cache empty) |

Trade-offs:

- **Cache lifetime = Renderer lifetime.** Cached `RhiShader` handles
  stay valid as long as the renderer does. The most common
  pre-existing pass disposal path (`PbrPass.Dispose`) no longer
  disposes its own `RhiShader` fields; the cache owns them. Pipelines
  (`RhiPipeline`) are still disposed by `PbrPass.Dispose` since each
  pass generates pipeline state specific to its `ScenePass`
  configuration.
- **TTL gating.** `EvictOlderThan(2)` runs on every
  `Renderer.ReloadPluginShaders` invocation. Content-equivalent
  entries get their generation tag refreshed on every cache hit, so
  hot entries survive eviction sweeps; truly-stale ones from prior
  generations drop out.
- **Cross-process.** The hash is computed from in-memory source
  bytes each lookup; on-disk `.dxil`/`.metallib` caching is out of
  scope for this commit.

## Disposal detection

The cache's `GetOrCompile` and `GetOrCompileHash` hit paths consult
[`RhiShader.IsAlive`](../engine_cs/Engine.RHI/RhiShader.cs) on the
cached wrapper before returning it. If the wrapper's `Handle` has been
zeroed by an external holder's `Dispose()` — typical when an earlier
plan's render-pass lifecycle tore down its own `_shader` field during
a hot reload or scene reload — the cache treats the entry as a miss
and re-runs the factory closure to produce a fresh wrapper.

The detection is necessary because the cache stores raw `IDisposable`
references (no proxy), and the original holder's `Dispose()` does not
inform the cache. Without the check a dead `RhiShader` would round
trip back into the next plan build's
[`RhiPipeline.CreateCompute`](../engine_cs/Engine.RHI/RhiPipeline.cs),
which now correctly throws `ObjectDisposedException` — but the
affected pass would still fail to register on the graph. Treating the
hit as a miss lets the cache recover transparently without invalidating
the calling renderer plan.

Cost: a single `is RhiShader` cast + one field read on the hit path,
in the low-microsecond range, dwarfed by the millisecond-scale Slang
recompile that would otherwise run on a content miss. The detection
is idempotent on healthy entries so the hit path latency budget is
unchanged.

## Known follow-ups

- **ReloadShaders needs refcount.** Once a pass grows a
  `ReloadShaders(args)` method (the path on the hot-reload roadmap),
  the cache must hand out a refcounted wrapper rather than the
  underlying `RhiShader` so disposing on eviction doesn't dangle
  the caller's reference. This commit ships the boot-time / phase-1
  path where every cached handle is consumed at most once per
  Renderer lifetime.
- **`GetOrCompileHash` factory signature asymmetry.** The legacy
  `GetOrCompile(string, Func<string, IDisposable>)` passes the
  `cacheKey` into the factory; the new `GetOrCompileHash(..., Func<IDisposable> factory)`
  does not, since by the time the factory runs the content-key has
  already been folded into the dictionary lookup outside the
  closure. A future API revision can align both to
  `Func<string, IDisposable>`.

## Cross-references

- `engine_cs/Engine.Renderer/Shaders/ShaderCompileCache.cs` —
  implementation.
- `engine_cs/Engine.Renderer/Renderer.cs` — owns the cache; bumps
  generation + evicts on plugin toggle.
- `engine_cs/Engine.Renderer/PbrPass.cs` — wires caches into
  every `RhiShader.FromSource` call.
- `Plugins/Renderer.Clustered/ClusteredRendererPlugin.cs` —
  threads `context.Renderer.ShaderCompileCache` into `PbrPass`.
- `engine_cs/Engine.RHI/RhiShader.FromSource` — the factory the
  cache wraps.
- `docs/renderer/ddgi.md` — primary beneficiary of the cache hit
  rate when toggling `renderer.ddgi`.
