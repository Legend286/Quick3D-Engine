# scripts/

Build orchestration scripts for macOS distribution. Bash-only; no PowerShell
needed because macOS is the only shippable target today (Vulkan/Windows is
post-MVP1 per `engine-spec.md` §23).

## Invocation

The scripts are committed with the executable bit set. When the bit is
lost (some git configurations strip it, or after a fresh clone on a
case-insensitive filesystem), fall back to:

```sh
bash scripts/<script>.sh
```

## Files

| Script | Purpose |
| --- | --- |
| `build-mac-app.sh` | End-to-end: cmake -> dotnet publish -> assemble `.app` -> codesign -> notarise -> staple. See `docs/build/mac-app-bundle.md` for the full pipeline + env-var matrix. |

## Common environment variables

See `docs/build/mac-app-bundle.md` -> "Environment variables" for the
full matrix. The most common subset:

| Var | Purpose |
| --- | --- |
| `APP_DISPLAY_NAME` | `.app` bundle + Info.plist display name. Default `EndEngine Editor`. |
| `BUNDLE_IDENTIFIER` | `CFBundleIdentifier` in Info.plist. Default `com.endengine.editor`. |
| `APP_VERSION` | `CFBundleShortVersionString`. Default `0.1.0`. |
| `BUILD_NUMBER` | `CFBundleVersion`. Default `1`. |
| `DOTNET_RUNTIME_ID` | `-r` arg to `dotnet publish`. Default `osx-arm64`. |
| `DEVELOPER_ID` | Sign identity for `codesign --sign`. Unset -> skip signing. |
| `KEYCHAIN_PROFILE` | `notarytool --keychain-profile`. Unset -> skip notarisation. |

## Why bash, not CMake or MSBuild

These scripts are **distribution glue**, not part of the build graph:

- They are not invoked from CMake so the C side builds independently of a
  macOS-aware host.
- They are not invoked from MSBuild so the .NET side builds independently of
  CMake fetched content.
- They assume a macOS host (codesign, notarytool, ditto). On other platforms
  `scripts/build-mac-app.sh` exits early with a clear error.
