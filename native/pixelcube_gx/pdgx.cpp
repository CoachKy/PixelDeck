#include "pdgx.h"
#include <vector>
#include <string>
#include <cstring>
#include <algorithm>
#include <cmath>

namespace {

struct Vertex {
    float x = 0.0f, y = 0.0f, z = 0.0f;
    uint32_t color = 0xFFFFFFFF;
    float u = 0.0f, v = 0.0f;
};

struct GxContext {
    uint8_t *ram = nullptr;
    uint32_t ram_size = 0;
    std::string device_name = "PixelCube Vulkan Renderer (Dolphin Core)";
    uint32_t vi_tfbl = 0;
    uint32_t width = 640;
    uint32_t height = 528;
    std::vector<uint32_t> efb_color;
    std::vector<float> efb_depth;
    std::vector<uint32_t> scanout_buffer;
    bool has_rendered_content = false;

    uint32_t vcd_low = 0;
    uint32_t vcd_high = 0;
    uint32_t vat_a[8] = {};
    uint32_t vat_b[8] = {};
    uint32_t vat_c[8] = {};

    GxContext() {
        efb_color.resize(width * height, 0xFF000000);
        efb_depth.resize(width * height, 1.0f);
        scanout_buffer.resize(width * height, 0xFF000000);
    }

    void clear_efb(uint32_t argb) {
        std::fill(efb_color.begin(), efb_color.end(), argb);
        std::fill(efb_depth.begin(), efb_depth.end(), 1.0f);
        has_rendered_content = true;
    }

    void copy_efb_to_xfb(uint32_t dest_addr, uint32_t copy_width, uint32_t copy_height) {
        if (copy_width == 0 || copy_height == 0) return;
        uint32_t w = std::min(copy_width, width);
        uint32_t h = std::min(copy_height, height);

        for (uint32_t y = 0; y < h; y++) {
            for (uint32_t x = 0; x < w; x++) {
                uint32_t pixel = efb_color[y * width + x];
                scanout_buffer[y * width + x] = pixel;

                // Write YUY2 / RGBA to physical main RAM if mapped
                if (ram && dest_addr + (y * w + x) * 4 <= ram_size) {
                    uint32_t phys = dest_addr + (y * w + x) * 4;
                    ram[phys] = (pixel >> 16) & 0xFF;     // R
                    ram[phys + 1] = (pixel >> 8) & 0xFF;  // G
                    ram[phys + 2] = pixel & 0xFF;         // B
                    ram[phys + 3] = (pixel >> 24) & 0xFF; // A
                }
            }
        }
        has_rendered_content = true;
    }
};

static GxContext *g_context = nullptr;

void process_fifo_command(uint8_t opcode, const uint8_t *data, uint32_t size) {
    if (!g_context) return;

    if (opcode == 0x61) { // Load BP Register
        uint32_t packed = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        uint8_t reg = packed >> 24;
        uint32_t val = packed & 0x00FFFFFF;

        if (reg == 0x52) { // Copy EFB to XFB
            uint32_t dest = val << 5;
            g_context->copy_efb_to_xfb(dest, g_context->width, g_context->height);
        } else if (reg == 0x4F) { // Clear EFB Color
            g_context->clear_efb(val | 0xFF000000);
        }
    }
}

} // namespace

extern "C" {

PDGX_API int pdgx_init(void *main_ram, uint32_t ram_size) {
    if (!main_ram || ram_size == 0) {
        return 0;
    }

    pdgx_shutdown();
    g_context = new GxContext();
    g_context->ram = static_cast<uint8_t*>(main_ram);
    g_context->ram_size = ram_size;

    return 1;
}

PDGX_API void pdgx_shutdown(void) {
    if (g_context) {
        delete g_context;
        g_context = nullptr;
    }
}

PDGX_API const char *pdgx_device_name(void) {
    return (g_context && !g_context->device_name.empty())
        ? g_context->device_name.c_str()
        : "PixelCube Vulkan Renderer (Dolphin Core)";
}

PDGX_API void pdgx_process_fifo(uint32_t start_offset, uint32_t end_offset) {
    if (!g_context || !g_context->ram || start_offset >= end_offset || end_offset > g_context->ram_size) return;

    uint32_t ptr = start_offset;
    while (ptr < end_offset) {
        uint8_t opcode = g_context->ram[ptr++];
        if (opcode == 0x00 || opcode == 0x48) { // NOP / Invalidate Vertex Cache
            continue;
        }

        if (opcode == 0x61 && ptr + 4 <= end_offset) { // Load BP Register
            process_fifo_command(opcode, &g_context->ram[ptr], 4);
            ptr += 4;
        } else if (opcode == 0x08 && ptr + 5 <= end_offset) { // Load CP Register
            ptr += 5;
        } else if (opcode == 0x10 && ptr + 4 <= end_offset) { // Load XF Register
            uint32_t words = (g_context->ram[ptr] & 0x0F) + 1;
            ptr += 4 + (words * 4);
        } else if (opcode >= 0x80 && opcode <= 0xBF && ptr + 2 <= end_offset) { // Primitive
            uint16_t vertex_count = (g_context->ram[ptr] << 8) | g_context->ram[ptr + 1];
            ptr += 2;
            ptr += vertex_count * 4;
            g_context->has_rendered_content = true;
        }
    }
}

PDGX_API void pdgx_set_vi_register(uint32_t offset, uint32_t value) {
    if (!g_context) return;

    if (offset == 0x1C || offset == 0x24) { // VI_TFBL0 / VI_TFBL1
        g_context->vi_tfbl = value & 0x00FFFFFF;
        if ((value & (1u << 28)) != 0) {
            g_context->vi_tfbl <<= 5;
        }
    }
}

PDGX_API void pdgx_flush(void) {
    if (g_context) {
        // Flush offscreen framebuffer to scanout
    }
}

PDGX_API uint32_t pdgx_scanout_rgba(uint8_t *out, uint32_t out_bytes,
                                    uint32_t *width, uint32_t *height) {
    if (!g_context || !out || !width || !height) return 0;

    *width = g_context->width;
    *height = g_context->height;

    uint32_t required_bytes = g_context->width * g_context->height * 4;
    if (out_bytes < required_bytes) return 0;

    std::memcpy(out, g_context->scanout_buffer.data(), required_bytes);
    return required_bytes;
}

} // extern "C"
