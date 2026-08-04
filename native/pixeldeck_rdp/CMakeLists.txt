cmake_minimum_required(VERSION 3.20)
project(pixeldeck_rdp CXX C)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Vendored parallel-rdp (MIT). The source list mirrors config.mk, which is how
# the standalone drop expects host projects to consume it.
set(PRDP "${CMAKE_CURRENT_SOURCE_DIR}/../../external/parallel-rdp-standalone")

if(NOT EXISTS "${PRDP}/parallel-rdp/rdp_device.hpp")
    message(FATAL_ERROR "parallel-rdp-standalone not found at ${PRDP}. "
                        "Clone it before configuring this project.")
endif()

file(GLOB PRDP_CORE_SOURCES "${PRDP}/parallel-rdp/*.cpp")

set(PRDP_VULKAN_SOURCES
    buffer.cpp buffer_pool.cpp command_buffer.cpp command_pool.cpp context.cpp
    cookie.cpp descriptor_set.cpp device.cpp event_manager.cpp fence.cpp
    fence_manager.cpp image.cpp indirect_layout.cpp memory_allocator.cpp
    pipeline_event.cpp query_pool.cpp render_pass.cpp sampler.cpp semaphore.cpp
    semaphore_manager.cpp shader.cpp)
list(TRANSFORM PRDP_VULKAN_SOURCES PREPEND "${PRDP}/vulkan/")
list(APPEND PRDP_VULKAN_SOURCES "${PRDP}/vulkan/texture/texture_format.cpp")

set(PRDP_UTIL_SOURCES
    arena_allocator.cpp logging.cpp thread_id.cpp aligned_alloc.cpp timer.cpp
    timeline_trace_file.cpp environment.cpp thread_name.cpp)
list(TRANSFORM PRDP_UTIL_SOURCES PREPEND "${PRDP}/util/")

add_library(pixeldeck_rdp SHARED
    pdrdp.cpp
    ${PRDP_CORE_SOURCES}
    ${PRDP_VULKAN_SOURCES}
    ${PRDP_UTIL_SOURCES}
    "${PRDP}/volk/volk.c")

target_include_directories(pixeldeck_rdp PRIVATE
    "${PRDP}/parallel-rdp"
    "${PRDP}/volk"
    "${PRDP}/vulkan"
    "${PRDP}/vulkan-headers/include"
    "${PRDP}/util")

if(WIN32)
    target_compile_definitions(pixeldeck_rdp PRIVATE VK_USE_PLATFORM_WIN32_KHR NOMINMAX)
    target_link_libraries(pixeldeck_rdp PRIVATE winmm)
    # parallel-rdp uses designated initializers and other constructs MSVC only
    # accepts under the conformant preprocessor and permissive- off.
    target_compile_options(pixeldeck_rdp PRIVATE /permissive- /Zc:preprocessor /bigobj /EHsc)
else()
    find_package(Threads REQUIRED)
    target_link_libraries(pixeldeck_rdp PRIVATE Threads::Threads ${CMAKE_DL_LIBS})
endif()

set_target_properties(pixeldeck_rdp PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
