cmake_minimum_required(VERSION 3.21)
project(SimLink LANGUAGES CXX)

# 32-bit only: it loads into TS3W.exe (PE32).
if(NOT CMAKE_SIZEOF_VOID_P EQUAL 4)
    message(FATAL_ERROR "SimLink must be built for x86 (cmake -A Win32)")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_library(simlink_ring STATIC src/ring.cpp)
target_include_directories(simlink_ring PUBLIC src)
target_compile_definitions(simlink_ring PUBLIC WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_compile_options(simlink_ring PUBLIC /W4 /EHa)

# The sidecar: an ASI plugin (a DLL the ASI loader in Game\Bin loads into every TS3W.exe).
add_library(SimLink SHARED src/sidecar.cpp src/sacs_patch.cpp src/gc_gate.cpp src/free_ring.cpp src/chunk_reserve.cpp
            src/onscreen.cpp src/gc_heap.cpp src/sequence_guard.cpp)
target_link_libraries(SimLink PRIVATE simlink_ring)
set_target_properties(SimLink PROPERTIES SUFFIX ".asi")

add_executable(simlink_selftest src/selftest.cpp)
target_link_libraries(simlink_selftest PRIVATE simlink_ring)
