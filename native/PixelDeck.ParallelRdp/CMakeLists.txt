cmake_minimum_required(VERSION 3.24)

project(PixelDeckParallelRdp VERSION 0.2.0 LANGUAGES C CXX)

include(FetchContent)
include(GNUInstallDirs)

option(
    PIXELDECK_PARALLEL_RDP_BUILD_TESTS
    "Build the native ABI smoke test."
    ON)
set(
    PIXELDECK_PARALLEL_RDP_SOURCE_DIR
    ""
    CACHE PATH
    "Optional local checkout of parallel-rdp-standalone.")

if(PIXELDECK_PARALLEL_RDP_SOURCE_DIR)
    FetchContent_Declare(
        parallel_rdp
        SOURCE_DIR "${PIXELDECK_PARALLEL_RDP_SOURCE_DIR}")
else()
    # This is the generated standalone integration tree. Its COMMIT file pins
    # the paraLLEl-RDP core to 1cecd042b2619bc505c12bfdc713808386f2b54d.
    FetchContent_Declare(
        parallel_rdp
        GIT_REPOSITORY https://github.com/Themaister/parallel-rdp-standalone.git
        GIT_TAG 388d70f5835b352d841d9d9e5a08c5de01470f41
        GIT_SHALLOW FALSE
        GIT_PROGRESS TRUE)
endif()

FetchContent_MakeAvailable(parallel_rdp)

file(
    GLOB
    PARALLEL_RDP_CORE_SOURCES
    CONFIGURE_DEPENDS
    "${parallel_rdp_SOURCE_DIR}/parallel-rdp/*.cpp")

set(
    PARALLEL_RDP_VULKAN_SOURCES
    "${parallel_rdp_SOURCE_DIR}/vulkan/buffer.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/buffer_pool.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/command_buffer.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/command_pool.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/context.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/cookie.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/descriptor_set.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/device.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/event_manager.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/fence.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/fence_manager.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/image.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/indirect_layout.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/memory_allocator.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/pipeline_event.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/query_pool.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/render_pass.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/sampler.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/semaphore.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/semaphore_manager.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/shader.cpp"
    "${parallel_rdp_SOURCE_DIR}/vulkan/texture/texture_format.cpp")

set(
    PARALLEL_RDP_UTIL_SOURCES
    "${parallel_rdp_SOURCE_DIR}/util/arena_allocator.cpp"
    "${parallel_rdp_SOURCE_DIR}/util/logging.cpp"
    "${parallel_rdp_SOURCE_DIR}/util/thread_id.cpp"
    "${parallel_rdp_SOURCE_DIR}/util/aligned_alloc.cpp"
    "${parallel_rdp_SOURCE_DIR}/util/timer.cpp"
    "${parallel_rdp_SOURCE_DIR}/util/timeline_trace_file.cpp"
    "${parallel_rdp_SOURCE_DIR}/util/environment.cpp"
    "${parallel_rdp_SOURCE_DIR}/util/thread_name.cpp")

find_package(Threads REQUIRED)

add_library(
    PixelDeck.ParallelRdp
    SHARED
    src/pixeldeck_parallel_rdp.cpp
    "${parallel_rdp_SOURCE_DIR}/volk/volk.c"
    ${PARALLEL_RDP_CORE_SOURCES}
    ${PARALLEL_RDP_VULKAN_SOURCES}
    ${PARALLEL_RDP_UTIL_SOURCES})

target_compile_features(PixelDeck.ParallelRdp PRIVATE cxx_std_17)
set_target_properties(
    PixelDeck.ParallelRdp
    PROPERTIES
        CXX_VISIBILITY_PRESET hidden
        C_VISIBILITY_PRESET hidden
        VISIBILITY_INLINES_HIDDEN YES)

target_include_directories(
    PixelDeck.ParallelRdp
    PRIVATE
        include
        "${parallel_rdp_SOURCE_DIR}/parallel-rdp"
        "${parallel_rdp_SOURCE_DIR}/volk"
        "${parallel_rdp_SOURCE_DIR}/vulkan"
        "${parallel_rdp_SOURCE_DIR}/vulkan-headers/include"
        "${parallel_rdp_SOURCE_DIR}/util")

target_link_libraries(PixelDeck.ParallelRdp PRIVATE Threads::Threads)

if(WIN32)
    target_compile_definitions(
        PixelDeck.ParallelRdp
        PRIVATE
            VK_USE_PLATFORM_WIN32_KHR
            NOMINMAX
            WIN32_LEAN_AND_MEAN
            _CRT_SECURE_NO_WARNINGS)
    target_link_libraries(PixelDeck.ParallelRdp PRIVATE winmm)
elseif(CMAKE_DL_LIBS)
    target_link_libraries(PixelDeck.ParallelRdp PRIVATE "${CMAKE_DL_LIBS}")
endif()

install(
    TARGETS PixelDeck.ParallelRdp
    RUNTIME DESTINATION "${CMAKE_INSTALL_BINDIR}"
    LIBRARY DESTINATION "${CMAKE_INSTALL_LIBDIR}")
install(
    FILES "${parallel_rdp_SOURCE_DIR}/LICENSE"
    DESTINATION "${CMAKE_INSTALL_DATADIR}/licenses/PixelDeck"
    RENAME "paraLLEl-RDP-LICENSE.txt")

if(PIXELDECK_PARALLEL_RDP_BUILD_TESTS)
    enable_testing()
    add_executable(
        PixelDeck.ParallelRdp.AbiSmoke
        tests/abi_smoke.cpp)
    target_compile_features(
        PixelDeck.ParallelRdp.AbiSmoke
        PRIVATE cxx_std_17)
    target_include_directories(
        PixelDeck.ParallelRdp.AbiSmoke
        PRIVATE include src)
    target_link_libraries(
        PixelDeck.ParallelRdp.AbiSmoke
        PRIVATE PixelDeck.ParallelRdp)
    add_test(
        NAME PixelDeck.ParallelRdp.AbiSmoke
        COMMAND PixelDeck.ParallelRdp.AbiSmoke)
endif()
