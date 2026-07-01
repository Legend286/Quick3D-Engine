# SPDX-License-Identifier: MIT
cmake_minimum_required(VERSION 3.25)

project(Cook)

add_executable(engine_cook main.cpp)
target_include_directories(engine_cook PRIVATE ${tinygltf_SOURCE_DIR})
target_compile_features(engine_cook PRIVATE cxx_std_17)
