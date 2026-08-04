#include "pdrdp.h"

#include "rdp_device.hpp"
#include "context.hpp"
#include "device.hpp"

#include <memory>
#include <vector>
#include <string>
#include <cstring>

namespace
{
struct PdRdpState
{
	Vulkan::Context context;
	Vulkan::Device device;
	std::unique_ptr<RDP::CommandProcessor> processor;
	std::vector<RDP::RGBA> scanout;
	std::string device_name;
};

std::unique_ptr<PdRdpState> g_state;
} // namespace

extern "C" {

int pdrdp_init(void *rdram, uint32_t rdram_size)
{
	if (g_state)
		return 1;
	if (!rdram || rdram_size == 0)
		return 0;

	if (!Vulkan::Context::init_loader(nullptr))
		return 0;

	auto state = std::make_unique<PdRdpState>();

	// Headless: no instance or device extensions, no surface.
	if (!state->context.init_instance_and_device(nullptr, 0, nullptr, 0))
		return 0;

	state->device.set_context(state->context);

	// Hidden RDRAM carries the 8-bit coverage/alpha plane the RDP keeps
	// alongside the frame buffer; hardware sizes it at one byte per 8 bytes
	// of RDRAM.
	const size_t hidden_size = rdram_size / 8;

	state->processor = std::make_unique<RDP::CommandProcessor>(
		state->device, rdram, 0, rdram_size, hidden_size, 0);

	if (!state->processor->device_is_supported())
		return 0;

	const auto &props = state->context.get_gpu_props();
	state->device_name = props.deviceName;

	g_state = std::move(state);
	return 1;
}

void pdrdp_shutdown(void)
{
	if (!g_state)
		return;
	g_state->processor->idle();
	g_state.reset();
}

const char *pdrdp_device_name(void)
{
	return g_state ? g_state->device_name.c_str() : "";
}

void pdrdp_enqueue_command(const uint32_t *words, uint32_t word_count)
{
	if (!g_state || !words || word_count == 0)
		return;
	g_state->processor->enqueue_command(word_count, words);
}

void pdrdp_set_vi_register(uint32_t index, uint32_t value)
{
	if (!g_state || index >= unsigned(RDP::VIRegister::Count))
		return;
	g_state->processor->set_vi_register(RDP::VIRegister(index), value);
}

void pdrdp_flush(void)
{
	if (!g_state)
		return;
	g_state->processor->flush();
}

uint32_t pdrdp_scanout_rgba(uint8_t *out, uint32_t out_bytes,
                            uint32_t *width, uint32_t *height)
{
	if (!g_state || !out)
		return 0;

	unsigned w = 0;
	unsigned h = 0;
	g_state->processor->scanout_sync(g_state->scanout, w, h, {});

	if (width)
		*width = w;
	if (height)
		*height = h;

	const uint32_t needed = uint32_t(w) * uint32_t(h) * 4u;
	if (needed == 0 || needed > out_bytes)
		return 0;

	std::memcpy(out, g_state->scanout.data(), needed);
	g_state->processor->begin_frame_context();
	return needed;
}

} // extern "C"
