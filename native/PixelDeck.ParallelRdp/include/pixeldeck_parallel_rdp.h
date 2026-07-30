#pragma once

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#define PD_PARALLEL_RDP_EXPORT __declspec(dllexport)
#else
#define PD_PARALLEL_RDP_EXPORT __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C"
{
#endif

typedef struct pd_parallel_rdp_context pd_parallel_rdp_context;

enum pd_parallel_rdp_status
{
    PD_PARALLEL_RDP_SUCCESS = 0,
    PD_PARALLEL_RDP_INVALID_ARGUMENT = -1,
    PD_PARALLEL_RDP_VULKAN_UNAVAILABLE = -2,
    PD_PARALLEL_RDP_DEVICE_UNSUPPORTED = -3,
    PD_PARALLEL_RDP_OUT_OF_MEMORY = -4,
    PD_PARALLEL_RDP_BUFFER_TOO_SMALL = -5,
    PD_PARALLEL_RDP_INTERNAL_ERROR = -6
};

enum pd_parallel_rdp_vi_register
{
    PD_PARALLEL_RDP_VI_CONTROL = 0,
    PD_PARALLEL_RDP_VI_ORIGIN = 1,
    PD_PARALLEL_RDP_VI_WIDTH = 2,
    PD_PARALLEL_RDP_VI_INTR = 3,
    PD_PARALLEL_RDP_VI_CURRENT_LINE = 4,
    PD_PARALLEL_RDP_VI_TIMING = 5,
    PD_PARALLEL_RDP_VI_V_SYNC = 6,
    PD_PARALLEL_RDP_VI_H_SYNC = 7,
    PD_PARALLEL_RDP_VI_LEAP = 8,
    PD_PARALLEL_RDP_VI_H_START = 9,
    PD_PARALLEL_RDP_VI_V_START = 10,
    PD_PARALLEL_RDP_VI_V_BURST = 11,
    PD_PARALLEL_RDP_VI_X_SCALE = 12,
    PD_PARALLEL_RDP_VI_Y_SCALE = 13,
    PD_PARALLEL_RDP_VI_REGISTER_COUNT = 14
};

/*
 * ABI version 2 owns an aligned 8 MiB RDRAM mirror and paraLLEl-RDP's 4 MiB
 * hidden-coverage mirror inside the native context.
 * Public RDRAM buffers use canonical N64 byte order; the bridge performs the
 * 32-bit word swap expected by paraLLEl-RDP on little-endian hosts.
 */
PD_PARALLEL_RDP_EXPORT uint32_t pd_parallel_rdp_get_abi_version(void);
PD_PARALLEL_RDP_EXPORT const char *pd_parallel_rdp_get_upstream_revision(void);
PD_PARALLEL_RDP_EXPORT const char *pd_parallel_rdp_get_last_error(void);

PD_PARALLEL_RDP_EXPORT int32_t pd_parallel_rdp_create(
    pd_parallel_rdp_context **context);
PD_PARALLEL_RDP_EXPORT void pd_parallel_rdp_destroy(
    pd_parallel_rdp_context *context);

PD_PARALLEL_RDP_EXPORT int32_t pd_parallel_rdp_upload_rdram(
    pd_parallel_rdp_context *context,
    const uint8_t *canonical_rdram,
    size_t size);
PD_PARALLEL_RDP_EXPORT int32_t pd_parallel_rdp_download_rdram(
    pd_parallel_rdp_context *context,
    uint8_t *canonical_rdram,
    size_t size);
PD_PARALLEL_RDP_EXPORT int32_t pd_parallel_rdp_upload_hidden_rdram(
    pd_parallel_rdp_context *context,
    const uint8_t *hidden_rdram,
    size_t size);
PD_PARALLEL_RDP_EXPORT int32_t pd_parallel_rdp_download_hidden_rdram(
    pd_parallel_rdp_context *context,
    uint8_t *hidden_rdram,
    size_t size);

PD_PARALLEL_RDP_EXPORT int32_t pd_parallel_rdp_begin_frame(
    pd_parallel_rdp_context *context);
PD_PARALLEL_RDP_EXPORT int32_t pd_parallel_rdp_enqueue_command(
    pd_parallel_rdp_context *context,
    const uint32_t *words,
    uint32_t word_count);
PD_PARALLEL_RDP_EXPORT int32_t pd_parallel_rdp_set_vi_register(
    pd_parallel_rdp_context *context,
    uint32_t register_index,
    uint32_t value);

/*
 * Scanout is deliberately split in two. The first call completes GPU work and
 * caches an RGBA8 frame; the second copies that exact frame without rendering
 * it a second time.
 */
PD_PARALLEL_RDP_EXPORT int32_t pd_parallel_rdp_scanout(
    pd_parallel_rdp_context *context,
    uint32_t *width,
    uint32_t *height);
PD_PARALLEL_RDP_EXPORT int32_t pd_parallel_rdp_copy_scanout(
    pd_parallel_rdp_context *context,
    uint8_t *rgba,
    size_t capacity,
    size_t *bytes_written);

#ifdef __cplusplus
}
#endif
