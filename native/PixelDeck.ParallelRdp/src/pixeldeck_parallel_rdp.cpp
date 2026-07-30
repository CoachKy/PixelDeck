#include "pixeldeck_parallel_rdp.h"
#include "rdram_layout.hpp"

#include "aligned_alloc.hpp"
#include "context.hpp"
#include "device.hpp"
#include "rdp_device.hpp"

#include <algorithm>
#include <cstring>
#include <exception>
#include <memory>
#include <mutex>
#include <new>
#include <stdexcept>
#include <string>
#include <vector>

namespace
{
constexpr uint32_t ABI_VERSION = 2;
constexpr size_t RDRAM_SIZE = 8u * 1024u * 1024u;
constexpr size_t HIDDEN_RDRAM_SIZE = RDRAM_SIZE / 2u;
// Intel's Windows Vulkan driver can require 64 KiB external-host alignment.
constexpr size_t RDRAM_ALIGNMENT = 64u * 1024u;
constexpr uint32_t MAX_COMMAND_WORDS = 64;
constexpr const char *UPSTREAM_REVISION =
    "parallel-rdp-standalone@388d70f5835b352d841d9d9e5a08c5de01470f41"
    " (parallel-rdp@1cecd042b2619bc505c12bfdc713808386f2b54d)";

thread_local std::string last_error;

int32_t fail(int32_t status, const char *message)
{
    last_error = message ? message : "Unknown paraLLEl-RDP bridge error.";
    return status;
}

int32_t fail_exception()
{
    try
    {
        throw;
    }
    catch (const std::bad_alloc &)
    {
        return fail(PD_PARALLEL_RDP_OUT_OF_MEMORY,
                    "The paraLLEl-RDP bridge could not allocate memory.");
    }
    catch (const std::exception &error)
    {
        last_error = error.what();
        return PD_PARALLEL_RDP_INTERNAL_ERROR;
    }
    catch (...)
    {
        return fail(PD_PARALLEL_RDP_INTERNAL_ERROR,
                    "The paraLLEl-RDP bridge caught an unknown native exception.");
    }
}

}

struct pd_parallel_rdp_context
{
    // Members are destroyed in reverse order. The processor must go first,
    // followed by RDRAM, Device, and finally its Vulkan Context.
    std::unique_ptr<Vulkan::Context> vulkan_context;
    std::unique_ptr<Vulkan::Device> device;
    std::unique_ptr<void, Util::AlignedDeleter> rdram;
    std::unique_ptr<RDP::CommandProcessor> processor;
    std::vector<RDP::RGBA> scanout;
    uint32_t scanout_width = 0;
    uint32_t scanout_height = 0;
    std::mutex mutex;
};

extern "C"
{
uint32_t pd_parallel_rdp_get_abi_version(void)
{
    return ABI_VERSION;
}

const char *pd_parallel_rdp_get_upstream_revision(void)
{
    return UPSTREAM_REVISION;
}

const char *pd_parallel_rdp_get_last_error(void)
{
    return last_error.c_str();
}

int32_t pd_parallel_rdp_create(pd_parallel_rdp_context **context)
{
    if (!context)
        return fail(PD_PARALLEL_RDP_INVALID_ARGUMENT,
                    "The output context pointer is null.");

    *context = nullptr;
    last_error.clear();
    try
    {
        if (!Vulkan::Context::init_loader(nullptr))
            return fail(PD_PARALLEL_RDP_VULKAN_UNAVAILABLE,
                        "The Vulkan loader could not be initialized.");

        auto result = std::make_unique<pd_parallel_rdp_context>();
        result->vulkan_context = std::make_unique<Vulkan::Context>();

        VkApplicationInfo application_info = {
            VK_STRUCTURE_TYPE_APPLICATION_INFO,
            nullptr,
            "Pixel64",
            VK_MAKE_VERSION(0, 15, 20),
            "PixelDeck paraLLEl-RDP bridge",
            ABI_VERSION,
            VK_API_VERSION_1_1
        };
        result->vulkan_context->set_application_info(&application_info);
        if (!result->vulkan_context->init_instance_and_device(
                nullptr, 0, nullptr, 0))
        {
            return fail(PD_PARALLEL_RDP_VULKAN_UNAVAILABLE,
                        "No Vulkan device could be initialized for paraLLEl-RDP.");
        }

        result->device = std::make_unique<Vulkan::Device>();
        result->device->set_context(*result->vulkan_context);

        result->rdram.reset(
            Util::memalign_calloc(RDRAM_ALIGNMENT, RDRAM_SIZE));
        if (!result->rdram)
            return fail(PD_PARALLEL_RDP_OUT_OF_MEMORY,
                        "The aligned 8 MiB RDRAM mirror could not be allocated.");

        result->processor = std::make_unique<RDP::CommandProcessor>(
            *result->device,
            result->rdram.get(),
            0,
            RDRAM_SIZE,
            HIDDEN_RDRAM_SIZE,
            0);
        if (!result->processor->device_is_supported())
        {
            return fail(
                PD_PARALLEL_RDP_DEVICE_UNSUPPORTED,
                "The Vulkan device does not expose paraLLEl-RDP's required features.");
        }

        *context = result.release();
        return PD_PARALLEL_RDP_SUCCESS;
    }
    catch (...)
    {
        return fail_exception();
    }
}

void pd_parallel_rdp_destroy(pd_parallel_rdp_context *context)
{
    try
    {
        delete context;
    }
    catch (...)
    {
        // Exceptions never cross the C ABI, including during process teardown.
    }
}

int32_t pd_parallel_rdp_upload_rdram(
    pd_parallel_rdp_context *context,
    const uint8_t *canonical_rdram,
    size_t size)
{
    if (!context || !canonical_rdram || size != RDRAM_SIZE)
        return fail(PD_PARALLEL_RDP_INVALID_ARGUMENT,
                    "RDRAM upload requires a context and exactly 8 MiB.");

    try
    {
        std::lock_guard<std::mutex> lock(context->mutex);
        context->processor->idle();
        PixelDeck::ParallelRdp::canonical_to_word_swapped(
            static_cast<uint8_t *>(context->rdram.get()),
            canonical_rdram,
            size);
        return PD_PARALLEL_RDP_SUCCESS;
    }
    catch (...)
    {
        return fail_exception();
    }
}

int32_t pd_parallel_rdp_download_rdram(
    pd_parallel_rdp_context *context,
    uint8_t *canonical_rdram,
    size_t size)
{
    if (!context || !canonical_rdram || size != RDRAM_SIZE)
        return fail(PD_PARALLEL_RDP_INVALID_ARGUMENT,
                    "RDRAM download requires a context and exactly 8 MiB.");

    try
    {
        std::lock_guard<std::mutex> lock(context->mutex);
        context->processor->idle();
        auto *mapped = static_cast<const uint8_t *>(
            context->processor->begin_read_rdram());
        if (!mapped)
            return fail(PD_PARALLEL_RDP_INTERNAL_ERROR,
                        "paraLLEl-RDP could not map RDRAM for readback.");

        PixelDeck::ParallelRdp::word_swapped_to_canonical(
            canonical_rdram,
            mapped,
            size);
        context->processor->end_write_rdram();
        return PD_PARALLEL_RDP_SUCCESS;
    }
    catch (...)
    {
        return fail_exception();
    }
}

int32_t pd_parallel_rdp_upload_hidden_rdram(
    pd_parallel_rdp_context *context,
    const uint8_t *hidden_rdram,
    size_t size)
{
    if (!context || !hidden_rdram || size != HIDDEN_RDRAM_SIZE)
        return fail(
            PD_PARALLEL_RDP_INVALID_ARGUMENT,
            "Hidden-RDRAM upload requires a context and exactly 4 MiB.");

    try
    {
        std::lock_guard<std::mutex> lock(context->mutex);
        context->processor->idle();
        auto *mapped = static_cast<uint8_t *>(
            context->processor->begin_read_hidden_rdram());
        if (!mapped)
            return fail(
                PD_PARALLEL_RDP_INTERNAL_ERROR,
                "paraLLEl-RDP could not map hidden RDRAM for upload.");

        std::memcpy(mapped, hidden_rdram, size);
        context->processor->end_write_hidden_rdram();
        return PD_PARALLEL_RDP_SUCCESS;
    }
    catch (...)
    {
        return fail_exception();
    }
}

int32_t pd_parallel_rdp_download_hidden_rdram(
    pd_parallel_rdp_context *context,
    uint8_t *hidden_rdram,
    size_t size)
{
    if (!context || !hidden_rdram || size != HIDDEN_RDRAM_SIZE)
        return fail(
            PD_PARALLEL_RDP_INVALID_ARGUMENT,
            "Hidden-RDRAM download requires a context and exactly 4 MiB.");

    try
    {
        std::lock_guard<std::mutex> lock(context->mutex);
        context->processor->idle();
        auto *mapped = static_cast<const uint8_t *>(
            context->processor->begin_read_hidden_rdram());
        if (!mapped)
            return fail(
                PD_PARALLEL_RDP_INTERNAL_ERROR,
                "paraLLEl-RDP could not map hidden RDRAM for readback.");

        std::memcpy(hidden_rdram, mapped, size);
        context->processor->end_write_hidden_rdram();
        return PD_PARALLEL_RDP_SUCCESS;
    }
    catch (...)
    {
        return fail_exception();
    }
}

int32_t pd_parallel_rdp_begin_frame(pd_parallel_rdp_context *context)
{
    if (!context)
        return fail(PD_PARALLEL_RDP_INVALID_ARGUMENT,
                    "The paraLLEl-RDP context is null.");

    try
    {
        std::lock_guard<std::mutex> lock(context->mutex);
        context->processor->begin_frame_context();
        context->scanout.clear();
        context->scanout_width = 0;
        context->scanout_height = 0;
        return PD_PARALLEL_RDP_SUCCESS;
    }
    catch (...)
    {
        return fail_exception();
    }
}

int32_t pd_parallel_rdp_enqueue_command(
    pd_parallel_rdp_context *context,
    const uint32_t *words,
    uint32_t word_count)
{
    if (!context || !words || word_count < 2 ||
        word_count > MAX_COMMAND_WORDS)
    {
        return fail(PD_PARALLEL_RDP_INVALID_ARGUMENT,
                    "An RDP command must contain between 2 and 64 words.");
    }

    try
    {
        std::lock_guard<std::mutex> lock(context->mutex);
        context->processor->enqueue_command(word_count, words);
        return PD_PARALLEL_RDP_SUCCESS;
    }
    catch (...)
    {
        return fail_exception();
    }
}

int32_t pd_parallel_rdp_set_vi_register(
    pd_parallel_rdp_context *context,
    uint32_t register_index,
    uint32_t value)
{
    if (!context || register_index >= PD_PARALLEL_RDP_VI_REGISTER_COUNT)
        return fail(PD_PARALLEL_RDP_INVALID_ARGUMENT,
                    "The VI register index is outside the supported range.");

    try
    {
        std::lock_guard<std::mutex> lock(context->mutex);
        context->processor->set_vi_register(
            static_cast<RDP::VIRegister>(register_index),
            value);
        return PD_PARALLEL_RDP_SUCCESS;
    }
    catch (...)
    {
        return fail_exception();
    }
}

int32_t pd_parallel_rdp_scanout(
    pd_parallel_rdp_context *context,
    uint32_t *width,
    uint32_t *height)
{
    if (!context || !width || !height)
        return fail(PD_PARALLEL_RDP_INVALID_ARGUMENT,
                    "Scanout requires a context and output dimensions.");

    try
    {
        std::lock_guard<std::mutex> lock(context->mutex);
        unsigned native_width = 0;
        unsigned native_height = 0;
        context->scanout.clear();
        context->processor->scanout_sync(
            context->scanout,
            native_width,
            native_height);
        context->scanout_width = native_width;
        context->scanout_height = native_height;
        *width = native_width;
        *height = native_height;
        return PD_PARALLEL_RDP_SUCCESS;
    }
    catch (...)
    {
        return fail_exception();
    }
}

int32_t pd_parallel_rdp_copy_scanout(
    pd_parallel_rdp_context *context,
    uint8_t *rgba,
    size_t capacity,
    size_t *bytes_written)
{
    if (!context || !bytes_written)
        return fail(PD_PARALLEL_RDP_INVALID_ARGUMENT,
                    "Scanout copy requires a context and byte-count output.");

    try
    {
        std::lock_guard<std::mutex> lock(context->mutex);
        const size_t required =
            context->scanout.size() * sizeof(RDP::RGBA);
        *bytes_written = required;
        if (required == 0)
            return PD_PARALLEL_RDP_SUCCESS;
        if (!rgba || capacity < required)
            return fail(PD_PARALLEL_RDP_BUFFER_TOO_SMALL,
                        "The RGBA scanout buffer is too small.");

        std::memcpy(rgba, context->scanout.data(), required);
        return PD_PARALLEL_RDP_SUCCESS;
    }
    catch (...)
    {
        return fail_exception();
    }
}
}
