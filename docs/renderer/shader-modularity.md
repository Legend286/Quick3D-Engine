# Renderer Shader Modularity

## Purpose

Enables managed engine plugins to ship Slang helper shaders that compose
with engine host shaders (e.g. `pbr.slang`) via Slang `#include` and
the preprocessor `#ifdef` toggles, without forking host source. The
transport mechanics (include-directory resolution, preprocessor
feature-set composition, compiled-shader caching) are documented here.
Plugin authors writing renderers that integrate via these hooks
should also read `docs/editor/extensions.md`.

A working example is `Plugins/Renderer.DDGI/`, the `renderer.ddgi`
plugin that demonstrates overriding host PBR's indirect-diffuse stub
with a DDGI-tinted placeholder when the plugin is enabled.

## Public API Surface

### Plugin manifest

A plugin's `plugin.json` adds two new fields:

- `shader_includes`: array of string paths, relative to the plugin's
  manifest directory. Slang `#include` resolution walks these in
  manifest-discovery order, then falls back to
  `<ContentRoot>/shaders/` lowest priority.

- `shader_features`: array of strings declaring the preprocessor
  macros the plugin contributes when enabled. Example:
  ```json
  {
    "id": "renderer.ddgi",
    "kind": "Renderer",
    "shader_includes": ["shaders"],
    "shader_features": ["DDGI_PLUGIN"]
  }
  ```

When this plugin is enabled, host shaders that gate behaviour on
`DDGI_PLUGIN` (e.g. `#ifdef DDGI_PLUGIN ... #include "ddgi_sampling.slang" ... #else ... #endif`)
pick up the plugin's override.

### C# managed bridge

- `RhiShader.FromSource(RhiDevice, string, string, ShaderStage,
  IReadOnlyList<string>? includeDirs, IReadOnlyList<string>? cliArgs)`
  primary overload. Companion helper `JoinCliArgs` whitespace-packs
  raw argv tokens (null -> null; single -> verbatim; multi ->
  single-space joined).

- `ShaderIncludeResolver.Resolve(IReadOnlyList<...plugins>, string
  contentRoot)` static helper in
  `engine_cs/Engine.Renderer/Shaders/`. Returns the prioritized
  include-dir chain (plugin manifests in discovery order, then engine
  fallback).

- `RendererFeatureSet.BuildCliArgs(IEnumerable<(EnginePluginManifest,
  bool IsEnabled)>?)` static helper that resolves enabled plugins'
  `shader_features` into the ordered argv list of `["-D","NAME=1", ...]`
  pairs.

- `ShaderCompileCache` generation-tracked in-process cache of
  `IDisposable` entries (in practice: `RhiShader` handles). Keys are
  stable strings keyed on `(content-root, source path, active feature
  set hash)`. Renderer owns one and bumps its generation on plugin
  toggle; entries older than 2 generations are disposed.

### Slang pattern

Host shaders register a stub function gated by `#ifdef`:

```slang
#ifdef PLUGIN_FEATURE
#include "plugin_override.slang"
#else
float3 ComputeIndirectLighting(float3 worldPosition, float3 worldNormal) {
    return float3(0, 0, 0);
}
#endif
```

Plugin overrides provide a same-signature function in a file shipped
via `shader_includes`. When the host compiles with `-D PLUGIN_FEATURE=1`
(because the plugin is enabled), the plugin's `plugin_override.slang`
resolves via the priority include-path chain and replaces the stub.

## Usage Example

The `renderer.ddgi` plugin is the canonical example. With the plugin
enabled, the renderer compiles host shaders with `-D DDGI_PLUGIN=1`
and `#include "ddgi_sampling.slang"` resolves to the plugin's override.

To author a new plugin:

1. Create `Plugins/MyPlugin/plugin.json` declaring `shader_includes`
   and `shader_features`.
2. Ship override files in `Plugins/MyPlugin/shaders/` (the directory
   referenced by `shader_includes`).
3. In the host shader, gate the override via:
   ```slang
   #ifdef MY_PLUGIN
   #include "my_override.slang"
   #else
   // stub fallback
   #endif
   ```
4. The renderer handles Slang `-D MY_PLUGIN=1` toggling automatically
   via `RendererFeatureSet.BuildCliArgs` + `RhiShader.FromSource`
   cliArgs threading.

## Performance Characteristics

- `ShaderCompileCache`: in-process, O(1) lookup with
  `StringComparer.Ordinal`. Memory cost: one entry per (source file +
  active feature set).
- `RendererFeatureSet.FeatureSetHash`: O(n) over the sorted feature
  list, n bounded by total plugin count.
- The Metal backend (`engine_c/rhi/rhi_metal.mm`) split-on-sentinel
  is O(file-size) on `include_path` and whitespace-tokenisation on
  `cli_args`. Negligible per compile.

## Cross-references

- `engine-spec.md` for the modular shader architecture decision.
- `docs/editor/extensions.md` for the plugin manifest schema.
- `docs/rhi/api.md` for the underlying `RhiShader.FromSource` ABI.
- `Content/shaders/pbr.slang` for canonical host stub usage.
- `Plugins/Renderer.DDGI/` for the running example.

## Notes

`ENGINE_ABI_VERSION_RHI` was bumped 11 -> 12 to accommodate the trailing
`RhiShaderDesc.cli_args` field (`engine_c/rhi/rhi.h`). All native-side
callers must be recompiled against the new ABI. The field is at the
end of the struct so intermediate offsets are unchanged.
