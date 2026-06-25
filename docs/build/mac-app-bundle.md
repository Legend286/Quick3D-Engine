# macOS App Bundle - Build, Sign, Notarise

> **Scope:** Phase 0 minimum-viable `.app` bundle for direct download on
> Apple Silicon. The Mac App Store flow (`pkgbuild` + `productbuild` +
> `xcrun altool` Transporter upload) is out of scope; tracked for a future
> phase once we have a real release pipeline in front of us.

## Purpose

Producing the Avalonia Editor as a runnable, code-signed, notarised macOS
`.app` bundle. Single entry point: `scripts/build-mac-app.sh`. Stages:

1. CMake builds `libEngineC.dylib` (the Metal RHI + D log ABI).
2. `cp` mirrors the dylib into `out/libEngineC.dylib` so
   `OutOfBand/Engine.CBindings.csproj`'s `<Content>` include finds it during
   `dotnet publish`.
3. `dotnet publish -c Release -r osx-arm64 --self-contained -p:PublishReadyToRun=true`
   produces a `.NET 8` self-contained R2R directory.
4. The directory is moved into `Foo.app/Contents/MacOS/`; `Editor/Info.plist`
   and `Editor/Entitlements.plist` are placed under `Contents/`.
5. `codesign --deep --force --options runtime --entitlements ...` signs the
   bundle with the Hardened Runtime enabled.
6. `ditto -c -k --sequesterRsrc --keepParent` produces a notary zip.
7. `xcrun notarytool submit --keychain-profile ... --wait` notarises.
8. `xcrun stapler staple <App>.app` staples the ticket so the .app works
   offline after first launch.
9. `codesign --verify --deep --strict` + `stapler validate` close the loop.

Each stage honours an env var so credentials absent locally never break the
build; CI injects credentials via standard secret env vars.

## Public API surface

### Files

| Path | Role |
| --- | --- |
| `scripts/build-mac-app.sh` | The full pipeline. Executable. |
| `Editor/Info.plist` | `CFBundle*` + `LSMinimumSystemVersion` + `LSApplicationCategoryType` metadata. Consumed by macOS at launch. |
| `Editor/Entitlements.plist` | Hardened-Runtime entitlements for the bundle (allow-jit, allow-unsigned-executable-memory). `disable-library-validation` is intentionally NOT shipped by default; only add it if `codesign --verify` rejects the bundle because `libEngineC.dylib` cannot satisfy library validation. |
| `Editor/Engine.Editor.csproj` | Gains `<RuntimeIdentifiers>osx-arm64;osx-x64</RuntimeIdentifiers>` so `dotnet publish -r <RID>` produces a RID-specific output. |

### Environment variables

| Var | Default | Effect when unset |
| --- | --- | --- |
| `APP_DISPLAY_NAME` | `EndEngine Editor` | - |
| `BUNDLE_IDENTIFIER` | `com.endengine.editor` | - |
| `APP_VERSION` | `0.1.0` | - |
| `BUILD_NUMBER` | `1` | - |
| `DOTNET_RUNTIME_ID` | `osx-arm64` | - |
| `DEVELOPER_ID` | - | Skip codesign with WARN (stage 4). |
| `KEYCHAIN_PROFILE` | - | Skip notarisation + staple (stage 6, 7). |
| `CMAKE_BUILD_DIR` | `out/build-smoke` | - |
| `PUBLISH_OUT_DIR` | `out/publish/<RID>` | - |

## Usage

### Local dry-run (no credentials needed)

```sh
./scripts/build-mac-app.sh
open "out/publish/osx-arm64/EndEngine Editor.app"
```

The .app is unsigned; on the developer machine Gatekeeper lets it launch
anyway (it would warn end users).

### Production release

```sh
# One-time: store notary credentials into a keychain profile.
xcrun notarytool store-credentials endengine-notary \
    --apple-id "ops@<your-org>.example" \
    --team-id "<TEAMID>" \
    --password "<app-specific-password>"

# Per release.
export DEVELOPER_ID="Developer ID Application: <Org Name> (<TEAMID>)"
export KEYCHAIN_PROFILE="endengine-notary"

./scripts/build-mac-app.sh
xcrun stapler validate "out/publish/osx-arm64/EndEngine Editor.app"
open "out/publish/osx-arm64/EndEngine Editor.app"
```

### Custom bundle identity

```sh
APP_DISPLAY_NAME="My Cool Editor" \
BUNDLE_IDENTIFIER="io.example.cool-editor" \
APP_VERSION="1.2.3" \
BUILD_NUMBER="42" \
./scripts/build-mac-app.sh
```

### Disabling Hardened-Runtime library validation (only if forced)

`libEngineC.dylib` is signed as part of the .app bundle via `codesign --deep`,
so library validation should pass natively and the `disable-library-validation`
entitlement is intentionally NOT shipped by default. The symptom that forces
the relaxation is `codesign --verify --deep --strict` failing with a
"library validation failed" message that names `libEngineC.dylib` as the
culprit (typically a developer-id vs ad-hoc-signature team mismatch). If
that happens, add the one key to `Editor/Entitlements.plist`:

```xml
<key>com.apple.security.cs.disable-library-validation</key>
<true/>
```

Library validation is one of Apple's primary hardened-runtime security gates;
keep it enabled unless a specific failure forces the relaxation.

### Universal binary (arm64 + x64)

Phase 0 ships arm64 only. To add x64:
1. Publish twice, once per RID.
2. Combine into a fat binary via `lipo -create`.
3. Re-sign the combined dylib + .app.

This is out of scope for the current scaffold; tracked under a follow-up.

## Bundle layout produced

```
<APP_DISPLAY_NAME>.app/
  Contents/
    Info.plist                 (from Editor/Info.plist; bundle metadata)
    MacOS/
      Engine.Editor            (PublishReadyToRun .NET host entry)
      *.dll                    (managed assemblies)
      *.deps.json
      host                     (the small dotnet host shim)
      libEngineC.dylib         (csproj Content Include; resolver finds it
                               via NativeLibraryLoader probing managed dir)
      resources/               (icons, locale, etc. - empty in Phase 0)
    Resources/                 (currently empty - reserved for icons, .icns)
    Frameworks/                (reserved for nested frameworks; unused now)
```

## Performance characteristics

| Stage | Time on M2 Pro |
| --- | --- |
| CMake (native dylib, incremental) | ~3 s |
| CMake (cold) | ~15 s |
| `dotnet publish` (R2R + self-contained, cold) | ~25 s |
| `dotnet publish` (incremental) | ~5 s |
| .app assembly + Info.plist + copy | <1 s |
| `codesign --deep` | ~2 s |
| `ditto` zip | ~1 s for ~120 MB .app |
| `notarytool submit --wait` | 30 s - 5 min (Apple cloud scan) |
| `stapler staple` | ~1 s |

Bundle size on disk (self-contained, R2R, arm64): **~120 MB** (dominated by
.NET 8 runtime + Avalonia native bits). Skipping `--self-contained` drops
it to ~30 MB but requires end users to have .NET 8 installed.

## Cross-references

- `engine-spec.md` §2.4 (Build system) - confirms CMake + `dotnet publish`
  are the canonical build pipeline.
- `engine-spec.md` §23 (Out of scope for MVP1) - Vulkan/Windows packaging
  not needed yet.
- `AGENTS.md` §3 (Documentation) - this file is the per-feature doc
  required for the build pipeline.
- `docs/rhi/metal.md` - why the dylib is loaded via dlopen (C ABI is the
  authoritative rendering surface).
- Apple docs:
  - [Distributing your app](https://developer.apple.com/documentation/xcode/distributing-your-app)
  - [notarytool](https://developer.apple.com/documentation/security/notarizing_macos_software_before_distribution)
  - [Hardened Runtime](https://developer.apple.com/documentation/security/hardened_runtime)

## Local validation without credentials

Run the script with no `DEVELOPER_ID` env var. The .app is produced but
unsigned. `open` still launches it locally on the developer machine. This
is the cheapest CI dry-run for verifying the bundle layout.
