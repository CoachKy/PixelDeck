cmake_minimum_required(VERSION 3.15)
project(pixelcube_gx CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_library(pixelcube_gx SHARED pdgx.cpp pdgx.h)

target_include_directories(pixelcube_gx PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})

if(WIN32)
    set_target_properties(pixelcube_gx PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/build/bin"
        LIBRARY_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/build/bin"
    )
endif()
