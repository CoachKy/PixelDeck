#include "pixeldeck_parallel_rdp.h"
#include "rdram_layout.hpp"

#include <array>
#include <cstring>

int main()
{
    if (pd_parallel_rdp_get_abi_version() != 2)
        return 1;

    const char *revision = pd_parallel_rdp_get_upstream_revision();
    if (!revision || std::strstr(revision, "388d70f") == nullptr)
        return 2;

    const std::array<uint8_t, 8> canonical = {
        0x01, 0x23, 0x45, 0x67,
        0x89, 0xab, 0xcd, 0xef
    };
    const std::array<uint8_t, 8> expected_swapped = {
        0x67, 0x45, 0x23, 0x01,
        0xef, 0xcd, 0xab, 0x89
    };
    std::array<uint8_t, 8> swapped = {};
    std::array<uint8_t, 8> round_trip = {};
    PixelDeck::ParallelRdp::canonical_to_word_swapped(
        swapped.data(),
        canonical.data(),
        canonical.size());
    if (swapped != expected_swapped)
        return 3;

    PixelDeck::ParallelRdp::word_swapped_to_canonical(
        round_trip.data(),
        swapped.data(),
        swapped.size());
    if (round_trip != canonical)
        return 4;

    std::array<uint8_t, 1> hidden = {};
    if (pd_parallel_rdp_upload_hidden_rdram(
            nullptr, hidden.data(), hidden.size()) !=
        PD_PARALLEL_RDP_INVALID_ARGUMENT)
    {
        return 5;
    }
    if (pd_parallel_rdp_download_hidden_rdram(
            nullptr, hidden.data(), hidden.size()) !=
        PD_PARALLEL_RDP_INVALID_ARGUMENT)
    {
        return 6;
    }

    // This smoke test deliberately does not create a Vulkan device. That is a
    // runtime capability test and must remain safe on headless CI machines.
    return 0;
}
