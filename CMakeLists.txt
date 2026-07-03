# SPDX-License-Identifier: MIT
cmake_minimum_required(VERSION 3.25)

set(SLANG_VERSION "2026.12")

project(EndEngine
    VERSION 0.1.0
    DESCRIPTION "Source-inspired game engine: C backend + C# userspace, Metal RHI"
    LANGUAGES C CXX OBJC OBJCXX)

set(CMAKE_C_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(APPLE)
    set(CMAKE_FRAMEWORK_PATH "" CACHE STRING "Frameworks path override")
    # Metal needs C++17 + Objective-C++.
    enable_language(OBJCXX)
    find_library(METAL_FRAMEWORK Metal REQUIRED)
    find_library(APPKIT_FRAMEWORK AppKit REQUIRED)
    find_library(QUARTZ_FRAMEWORK Quartz REQUIRED)
    find_library(FOUNDATION_FRAMEWORK Foundation REQUIRED)
    set(ENGINE_PLATFORMS "metal")
elseif(WIN32)
    set(ENGINE_PLATFORMS "vulkan_stub")  # Vulkan implementation deferred.
else()
    set(ENGINE_PLATFORMS "vulkan_stub")
endif()

option(ENGINE_FETCHCONTENT "Use CMake FetchContent to pull third_party deps" ON)
option(ENGINE_VENDORED_THIRDPARTY "Use vendored third_party stubs (offline mode)" OFF)

set(THIRD_PARTY_DIR ${CMAKE_SOURCE_DIR}/third_party CACHE PATH "third_party location")

# ----------------------------------------------------------------------------
# third_party pins (see docs/asset-pipeline/tags.md rationale).
# ----------------------------------------------------------------------------

if(ENGINE_FETCHCONTENT AND NOT ENGINE_VENDORED_THIRDPARTY)
    include(FetchContent)
    set(FETCHCONTENT_QUIET FALSE)

    # FLECS - pure C API. v4.1.5 is the stable release of v4 line.
    FetchContent_Declare(flecs
        GIT_REPOSITORY https://github.com/SanderMertens/flecs.git
        GIT_TAG v4.1.5
        GIT_SHALLOW TRUE)

    # cglm - pure C math.
    FetchContent_Declare(cglm
        GIT_REPOSITORY https://github.com/recp/cglm.git
        GIT_TAG v0.9.6
        GIT_SHALLOW TRUE)

    # MikkTSpace - tangent-space generator.
    FetchContent_Declare(mikktspace
        GIT_REPOSITORY https://github.com/mmikk/MikkTSpace.git
        GIT_TAG 3e895b4)

    # xatlas - UV/lightmap unwrap.
    FetchContent_Declare(xatlas
        GIT_REPOSITORY https://github.com/jpcy/xatlas.git
        GIT_TAG f700c77)

    # tinygltf v2 (v3 is experimental; stay on v2 for stability).
    FetchContent_Declare(tinygltf
        GIT_REPOSITORY https://github.com/syoyo/tinygltf.git
        GIT_TAG v2.9.7
        GIT_SHALLOW TRUE)

    # Basis Universal - master texture compressor.
    FetchContent_Declare(basis_universal
        GIT_REPOSITORY https://github.com/BinomialLLC/basis_universal.git
        GIT_TAG v2_10_final_snapshot
        GIT_SHALLOW TRUE)

    # VHACD - convex decomposition.
    FetchContent_Declare(vhacd
        GIT_REPOSITORY https://github.com/Unity-Technologies/VHACD.git
        GIT_TAG 854b3d8)

    # Jolt Physics v5 - used as C++ wrapped behind `extern "C"`.
    FetchContent_Declare(jolt
        GIT_REPOSITORY https://github.com/jrouwe/JoltPhysics.git
        GIT_TAG v5.5.0
        GIT_SHALLOW TRUE)

    # miniaudio - audio decoder + playback.
    FetchContent_Declare(miniaudio
        GIT_REPOSITORY https://github.com/mackron/miniaudio.git
        GIT_TAG 0.11.25
        GIT_SHALLOW TRUE)

    # Slang - shader cross compiler
    FetchContent_Declare(slang
        URL https://github.com/shader-slang/slang/releases/download/v${SLANG_VERSION}/slang-${SLANG_VERSION}-macos-aarch64.zip)

    FetchContent_MakeAvailable(flecs cglm mikktspace xatlas tinygltf basis_universal vhacd miniaudio slang)
    # Jolt is built separately to keep its C++ standard strict.
endif()

# ----------------------------------------------------------------------------
# engine_c - C/C++ core. Outputs libEngineC.{dylib,so,dll}.
# ----------------------------------------------------------------------------

add_library(EngineC
    SHARED  # dlopen'd at C# runtime via DllImport("EngineC") - the static
            # NativeReferences path was retired when the binding csproj moved
            # to <Content Include> of the dylib. .a would not satisfy P/Invoke.
    engine_c/engine_log.c
    engine_c/engine_ecs.c
    engine_c/engine_mem.c
    engine_c/rhi/rhi_dispatch.c
    engine_c/input/input_dispatch.c
)
if(APPLE)
    target_sources(EngineC PRIVATE
        engine_c/rhi/rhi_metal.mm
        engine_c/input/input_mac.m)
    target_link_libraries(EngineC PRIVATE
        ${METAL_FRAMEWORK}
        ${APPKIT_FRAMEWORK}
        ${QUARTZ_FRAMEWORK}
        ${FOUNDATION_FRAMEWORK})
elseif(WIN32)
    target_sources(EngineC PRIVATE
        engine_c/rhi/rhi_vulkan_stub.c
        engine_c/input/input_win32.c)
elseif(ENGINE_FETCHCONTENT)
    target_sources(EngineC PRIVATE
        engine_c/rhi/rhi_vulkan_stub.c
        engine_c/input/input_linux.c)
endif()

target_link_libraries(EngineC PRIVATE flecs)
if(APPLE)
    # Link Slang dynamically
    target_link_libraries(EngineC PRIVATE ${slang_SOURCE_DIR}/lib/libslang.dylib)
endif()

target_include_directories(EngineC
    PUBLIC  ${CMAKE_CURRENT_SOURCE_DIR}/engine_c
    PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/engine_c/rhi
            ${slang_SOURCE_DIR}/include)

target_compile_options(EngineC PRIVATE
    -Wall -Wextra -Wpedantic
    -Wno-unused-parameter
    $<$<COMPILE_LANGUAGE:C>:-Wno-unused-variable -Wno-unused-function -Wno-implicit-fallthrough>
    $<$<COMPILE_LANGUAGE:OBJC>:-fobjc-arc>
    $<$<COMPILE_LANGUAGE:OBJCXX>:-fobjc-arc>)

# Make dylib install_name portable so .NET Runtime (LibraryImport("EngineC"))
# can resolve it through @rpath lookup, not a stale absolute build path.
set_target_properties(EngineC PROPERTIES
    MACOSX_RPATH ON
    INSTALL_NAME_DIR "@rpath"
    BUILD_RPATH   "@loader_path")

if(APPLE AND ENGINE_FETCHCONTENT)
    # Copy libslang.dylib to the build directory so C# bindings can load it
    add_custom_command(TARGET EngineC POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy
            ${slang_SOURCE_DIR}/lib/libslang.dylib
            $<TARGET_FILE_DIR:EngineC>/libslang.dylib
        COMMENT "Copying libslang.dylib to output directory")
endif()

# ----------------------------------------------------------------------------
# Jolt Physics - thin C-export wrapper. Built in Phase 3 once the Jolt
# binding surface is locked. For now leave a stub so the link graph is stable.
# ----------------------------------------------------------------------------

# engine_c/physics/jolt_wrap.c will be added in Phase 3 with the
# EngineJoltWrap static lib + its Jolt::Jolt link dependency.

# ----------------------------------------------------------------------------
# Cook CLI
# ----------------------------------------------------------------------------
add_subdirectory(Cook)

# ----------------------------------------------------------------------------
# Install rules - none for now, builder uses the build/ tree directly.
# ----------------------------------------------------------------------------

add_executable(TestEngineMem engine_c/test_engine_mem.c engine_c/engine_mem.c)
target_include_directories(TestEngineMem PRIVATE engine_c)

enable_testing()
add_test(NAME MemoryTests COMMAND TestEngineMem)

message(STATUS "EndEngine ${PROJECT_VERSION} configured")
message(STATUS "  Platform targets: ${ENGINE_PLATFORMS}")
message(STATUS "  FetchContent:     ${ENGINE_FETCHCONTENT}")
