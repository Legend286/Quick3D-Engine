#!/usr/bin/env bash
# SPDX-License-Identifier: MIT
#
# End-to-end macOS distribution pipeline.
#
# Stages: cmake (native dylib) -> dotnet publish (self-contained R2R) -> .app
# bundle assembly -> codesign (Hardened Runtime) -> ditto -> notarytool ->
# stapler. Each stage honours an env var so credentials absent locally does
# not break the build; CI sets DEVELOPER_ID + KEYCHAIN_PROFILE.
#
# See docs/build/mac-app-bundle.md "Environment variables" for the matrix.

set -euo pipefail

# ---- locate project root (script lives at scripts/build-mac-app.sh) --------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
cd "${PROJECT_ROOT}"

# ---- env-var-driven inputs (defaults per docs/build/mac-app-bundle.md) -----
APP_DISPLAY_NAME="${APP_DISPLAY_NAME:-EndEngine Editor}"
BUNDLE_IDENTIFIER="${BUNDLE_IDENTIFIER:-com.endengine.editor}"
APP_VERSION="${APP_VERSION:-0.1.0}"
BUILD_NUMBER="${BUILD_NUMBER:-1}"
DOTNET_RUNTIME_ID="${DOTNET_RUNTIME_ID:-osx-arm64}"
DEVELOPER_ID="${DEVELOPER_ID:-}"
KEYCHAIN_PROFILE="${KEYCHAIN_PROFILE:-}"
CMAKE_BUILD_DIR="${CMAKE_BUILD_DIR:-out/build-smoke}"
PUBLISH_OUT_DIR="${PUBLISH_OUT_DIR:-out/publish/${DOTNET_RUNTIME_ID}}"
APP_BUNDLE_DIR="${PUBLISH_OUT_DIR}/${APP_DISPLAY_NAME}.app"
APP_BUNDLE_ZIP="${PUBLISH_OUT_DIR}/${APP_DISPLAY_NAME}.zip"

# ---- logging helpers --------------------------------------------------------
log_section() { printf "\n=== %s ===\n" "${1}"; }
log_warn()    { printf "WARN: %s\n" "${1}"; }
log_info()    { printf "  -> %s\n" "${1}"; }

# ---- preflight: host OS + toolchain ----------------------------------------
if [[ "$(uname -s)" != "Darwin" ]]; then
    printf "ERROR: this script only runs on macOS (Darwin).\n" >&2
    exit 2
fi
if ! command -v dotnet >/dev/null; then
    printf "ERROR: dotnet not on PATH.\n" >&2
    exit 2
fi
if ! command -v cmake >/dev/null; then
    printf "ERROR: cmake not on PATH.\n" >&2
    exit 2
fi

# ---- stage 1: cmake (native dylib) -----------------------------------------
log_section "Stage 1/8 - CMake native dylib"
mkdir -p "${CMAKE_BUILD_DIR}"
cmake -S "${PROJECT_ROOT}" -B "${CMAKE_BUILD_DIR}" \
    -DCMAKE_BUILD_TYPE=Release \
    -DBUILD_TESTING=OFF >/dev/null
cmake --build "${CMAKE_BUILD_DIR}" --target EngineC --parallel

# ---- stage 1b: mirror dylib to out/libEngineC.dylib (csproj Content path) -
log_section "Stage 1b/8 - Mirror dylib to out/libEngineC.dylib"
DYLIB_SRC="${CMAKE_BUILD_DIR}/libEngineC.dylib"
DYLIB_OUT="${PROJECT_ROOT}/out/libEngineC.dylib"
if [[ ! -f "${DYLIB_SRC}" ]]; then
    printf "ERROR: expected ${DYLIB_SRC} after CMake build; not found.\n" >&2
    exit 3
fi
mkdir -p "${PROJECT_ROOT}/out"
cp -f "${DYLIB_SRC}" "${DYLIB_OUT}"
log_info "${DYLIB_OUT}"

# ---- stage 2: dotnet publish ------------------------------------------------
log_section "Stage 2/8 - dotnet publish -c Release -r ${DOTNET_RUNTIME_ID}"
PUBLISH_FLAT="${PUBLISH_OUT_DIR}/_publish_flat"
rm -rf "${PUBLISH_FLAT}"
mkdir -p "${PUBLISH_FLAT}"
dotnet publish "${PROJECT_ROOT}/Editor/Engine.Editor.csproj" \
    -c Release \
    -r "${DOTNET_RUNTIME_ID}" \
    -p:PublishReadyToRun=true \
    -p:SelfContained=true \
    -p:DebugType=embedded \
    -o "${PUBLISH_FLAT}"

# ---- stage 3: assemble .app bundle -----------------------------------------
log_section "Stage 3/8 - Assemble .app bundle at ${APP_BUNDLE_DIR}"
rm -rf "${APP_BUNDLE_DIR}"
mkdir -p "${APP_BUNDLE_DIR}/Contents/MacOS"
mkdir -p "${APP_BUNDLE_DIR}/Contents/Resources"
cp -R "${PUBLISH_FLAT}/." "${APP_BUNDLE_DIR}/Contents/MacOS/"
cp "${PROJECT_ROOT}/Editor/Info.plist" "${APP_BUNDLE_DIR}/Contents/Info.plist"
log_info "Contents/MacOS/Engine.Editor (.NET host)"
log_info "Contents/MacOS/libEngineC.dylib (from csproj Content Include)"
log_info "Contents/Info.plist (bundle metadata)"

# ---- stage 4: code sign (Hardened Runtime + ad-hoc fallback) ---------------
#
# Apple Silicon refuses to load executable pages in an unsigned bundle (AMFI
# SIGKILL the process before main() runs - that's the Dock bounce-and-quit
# symptom when this stage is skipped). We always produce *some* signature so
# the bundle is structurally valid:
#
#   * Developer ID + Hardened Runtime + notarisation -> production.
#   * Ad-hoc signing without --options runtime      -> local dev only.
#
# Ad-hoc signing intentionally drops --options runtime AND --entitlements:
#   * Hardened Runtime requires DJSP / Developer-Team validation which only
#     works with a real Developer ID. Ad-hoc + --options runtime is rejected
#     by codesign ("invalid entitlements / signature"), so we don't try.
#   * Without --options runtime, library validation is off, JIT runs without
#     explicit allow-jit/allow-unsigned-executable-memory entitlements, and
#     the .NET 8 CoreCLR / Avalonia.Native dylib / libEngineC.dylib all share
#     a single ad-hoc identity so embedding checks pass.
if [[ -z "${DEVELOPER_ID}" ]]; then
    log_warn "DEVELOPER_ID not set - applying ad-hoc signature for local dev."
    log_warn "  Hardened Runtime + notarisation are disabled in this build."
    log_warn "  To enable, export both env vars described in docs/build/mac-app-bundle.md:"
    log_warn "      DEVELOPER_ID=\"Developer ID Application: <Team> (<TEAMID>)\""
    log_warn "      KEYCHAIN_PROFILE=<keychain-notary-profile>"
    log_section "Stage 4/8 - codesign --deep --force --sign - (ad-hoc)"
    codesign --deep --force --sign - \
        "${APP_BUNDLE_DIR}"
    SIGNED=2
else
    log_section "Stage 4/8 - codesign --deep --force --options runtime"
    codesign --deep --force --options runtime \
        --entitlements "${PROJECT_ROOT}/Editor/Entitlements.plist" \
        --sign "${DEVELOPER_ID}" \
        --timestamp \
        "${APP_BUNDLE_DIR}"
    SIGNED=1
fi

# ---- stage 5: ditto zip for notarytool submission --------------------------
log_section "Stage 5/8 - ditto zip for notarytool"
rm -f "${APP_BUNDLE_ZIP}"
ditto -c -k --sequesterRsrc --keepParent \
    "${APP_BUNDLE_DIR}" "${APP_BUNDLE_ZIP}"
log_info "${APP_BUNDLE_ZIP}"

# ---- stage 6: notarytool submit + stage 7: stapler staple + stage 8: verify
if [[ "${SIGNED}" -eq 2 ]]; then
    log_warn "App is ad-hoc-signed (DEVELOPER_ID not set); notarisation + stapling disabled."
elif [[ -z "${KEYCHAIN_PROFILE}" ]]; then
    log_warn "Notary credentials (KEYCHAIN_PROFILE) not set; skipping notarisation + stapling."
    log_warn "  Run 'xcrun notarytool store-credentials <profile>' once, then re-run with both env vars."
else
    log_section "Stage 6/8 - notarytool submit --keychain-profile ${KEYCHAIN_PROFILE}"
    xcrun notarytool submit "${APP_BUNDLE_ZIP}" \
        --keychain-profile "${KEYCHAIN_PROFILE}" \
        --wait

    log_section "Stage 7/8 - stapler staple"
    xcrun stapler staple "${APP_BUNDLE_DIR}"

    log_section "Stage 8/8 - verify"
    codesign --verify --deep --strict --verbose=2 "${APP_BUNDLE_DIR}"
    xcrun stapler validate "${APP_BUNDLE_DIR}"
fi

# ---- stage 9: strip Gatekeeper quarantine xattr -----------------------------
#
# `xattr -dr com.apple.quarantine` zeros the kernel-recorded download
# provenance so Finder doesn't bounce the bundle with the "is damaged and
# can't be opened" dialog + an extra "right-click Open" prompt for first
# launch on the developer machine. Local-only; notarisation gives the same
# effect for end-users via the stapled ticket, so this is safe.
log_section "Stage 9/9 - Strip Gatekeeper quarantine xattr"
xattr -dr com.apple.quarantine "${APP_BUNDLE_DIR}" 2>/dev/null || true
xattr -dr com.apple.provenance  "${APP_BUNDLE_DIR}" 2>/dev/null || true
log_info "xattr quarantine + provenance stripped"

# ---- wrap-up ----------------------------------------------------------------
log_section "Done"
printf "  App bundle : %s\n" "${APP_BUNDLE_DIR}"
printf "  Notary zip : %s\n" "${APP_BUNDLE_ZIP}"
if [[ "${SIGNED}" -eq 2 ]]; then
    printf "  Sign mode  : ad-hoc (DEVELOPER_ID unset) - local-only\n"
elif [[ "${SIGNED}" -eq 1 ]]; then
    printf "  Sign mode  : Developer ID + Hardened Runtime\n"
fi
printf "  Inspect    : codesign -dv --verbose=4 %q\n" "${APP_BUNDLE_DIR}"
printf "  Launch     : open %q\n" "${APP_BUNDLE_DIR}"
printf "           or: %q/Contents/MacOS/Engine.Editor\n" "${APP_BUNDLE_DIR}"
