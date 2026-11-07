cmake_minimum_required(VERSION 3.25)

project(EmmaSpaceNative VERSION 0.1.0 LANGUAGES CXX) # x-release-please-version

option(EMMASCAN_BUILD_TESTS "Build the emmascan unit tests" ON)
set(EMMASCAN_OUTPUT_DIR "" CACHE PATH "Directory that receives the emmascan shared library")

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Link the C++ runtime statically so the DLL has no VC++ redistributable dependency.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

if(EMMASCAN_BUILD_TESTS)
    enable_testing()
endif()

add_subdirectory(native/emmascan)
