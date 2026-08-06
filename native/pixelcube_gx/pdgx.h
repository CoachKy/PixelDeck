// Minimal C ABI over Dolphin's Vulkan rendering architecture, sized for PixelDeck / PixelCube.
//
// PixelDeck composites its own frame and has no swapchain to hand over, so
// this deliberately exposes only the headless path: feed native GX FIFO command
// streams in, read finished frames back out on the CPU. No window, no WSI, no
// input, no save states.

#pragma once

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define PDGX_API __declspec(dllexport)
#else
#define PDGX_API __attribute__((visibility("default")))
#endif

/// Creates a headless Vulkan device and binds the renderer to GameCube main memory.
/// `main_ram` must stay alive and at a fixed address for the session.
/// Returns 1 on success, 0 if no usable Vulkan device is present.
PDGX_API int pdgx_init(void *main_ram, uint32_t ram_size);

PDGX_API void pdgx_shutdown(void);

/// Human-readable device description, or "" when uninitialised.
PDGX_API const char *pdgx_device_name(void);

/// Submits a block of Flipper GX FIFO commands from `start_offset` to `end_offset`.
PDGX_API void pdgx_process_fifo(uint32_t start_offset, uint32_t end_offset);

/// Updates a Video Interface (VI) register value.
PDGX_API void pdgx_set_vi_register(uint32_t offset, uint32_t value);

/// Flushes queued work to the Vulkan GPU.
PDGX_API void pdgx_flush(void);

/// Scans out the current frame into `out` as tightly packed RGBA8.
/// Writes dimensions to `width`/`height`. Returns bytes written, or 0 on failure.
PDGX_API uint32_t pdgx_scanout_rgba(uint8_t *out, uint32_t out_bytes,
                                    uint32_t *width, uint32_t *height);

#ifdef __cplusplus
}
#endif
