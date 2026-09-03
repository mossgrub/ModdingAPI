cmake_minimum_required(VERSION 3.10) 
project(modding_native CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(modding_native SHARED modding_native.cpp)
target_compile_options(modding_native PRIVATE -fexceptions -fpic -O2)
target_link_libraries(modding_native PRIVATE log dl)

set_target_properties(modding_native PROPERTIES
    CXX_VISIBILITY_PRESET default
    VISIBILITY_INLINES_HIDDEN OFF
)
