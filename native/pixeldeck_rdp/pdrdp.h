// Minimal C ABI over Themaister's parallel-rdp, sized for PixelDeck.
//
// PixelDeck composites its own frame and has no swapchain to hand over, so
// this deliberately exposes only the headless path: feed native RDP command
// packets in, read finished frames back out on the CPU. No window, no WSI, no
// input, no save states.
//
// parallel-rdp is MIT licensed; see external/parallel-rdp-standalone/LICENSE.

#pragma once

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define PDRDP_API __declspec(dllexport)
#else
#define PDRDP_API __attribute__((visibility("default")))
#endif

/// Creates a headless Vulkan device and binds the RDP to the caller's RDRAM.
/// `rdram` must stay alive and at a fixed address for the session: the GPU
/// reads it directly rather than through a copy.
/// Returns 1 on success, 0 if no usable Vulkan device is present.
PDRDP_API int pdrdp_init(void *rdram, uint32_t rdram_size);

PDRDP_API void pdrdp_shutdown(void);

/// Human-readable device description, or "" when uninitialised.
PDRDP_API const char *pdrdp_device_name(void);

/// Submits one native RDP command packet (as 32-bit words).
PDRDP_API void pdrdp_enqueue_command(const uint32_t *words, uint32_t word_count);

/// VI register index matches RDP::VIRegister: 0 Control, 1 Origin, 2 Width,
/// 3 Intr, 4 VCurrentLine, 5 Timing, 6 VSync, 7 HSync, 8 Leap, 9 HStart,
/// 10 VStart, 11 VBurst, 12 XScale, 13 YScale.
PDRDP_API void pdrdp_set_vi_register(uint32_t index, uint32_t value);

/// Drains queued work.
PDRDP_API void pdrdp_flush(void);

/// Scans out the current frame into `out` as tightly packed RGBA8.
/// Writes the produced dimensions to `width`/`height`. Returns the number of
/// bytes written, or 0 if there was no frame or the buffer was too small.
PDRDP_API uint32_t pdrdp_scanout_rgba(uint8_t *out, uint32_t out_bytes,
                                      uint32_t *width, uint32_t *height);

#ifdef __cplusplus
}
#endif
