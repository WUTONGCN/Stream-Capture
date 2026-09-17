cmake_minimum_required(VERSION 3.15)
project(KuaishouHook LANGUAGES C CXX)
if(NOT WIN32)
  message(FATAL_ERROR "NativeHook requires Windows")
endif()
add_library(minhook STATIC
  minhook/src/buffer.c minhook/src/hook.c minhook/src/trampoline.c
  minhook/src/hde/hde32.c minhook/src/hde/hde64.c)
target_include_directories(minhook PUBLIC minhook/include)
add_library(KuaishouHook SHARED HookDll.cpp)
target_compile_features(KuaishouHook PRIVATE cxx_std_17)
target_link_libraries(KuaishouHook PRIVATE minhook ws2_32 winhttp)
set_target_properties(KuaishouHook PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
  RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/bin/Release")
