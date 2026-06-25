# `Cook/`

Standalone cook CLI. Ingests project source assets and emits cooked (binary, tagged-format) artefacts plus a SHA-256 manifest.

Flags (planned):

```sh
cook --project <path>      # cook the project at <path>
cook --project <path> --target <platform>
cook --project <path> --config Debug|Release
```

Output: `out/cook/<project_id>/<config>/`. Manifest at `manifest.json`. See [`docs/asset-pipeline/cook.md`](../docs/asset-pipeline/cook.md).
