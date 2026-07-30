#pragma once

#include <stddef.h>
#include <stdint.h>

namespace PixelDeck::ParallelRdp
{
inline void canonical_to_word_swapped(
    uint8_t *destination,
    const uint8_t *source,
    size_t size)
{
    for (size_t offset = 0; offset < size; offset += sizeof(uint32_t))
    {
        destination[offset + 0] = source[offset + 3];
        destination[offset + 1] = source[offset + 2];
        destination[offset + 2] = source[offset + 1];
        destination[offset + 3] = source[offset + 0];
    }
}

inline void word_swapped_to_canonical(
    uint8_t *destination,
    const uint8_t *source,
    size_t size)
{
    canonical_to_word_swapped(destination, source, size);
}
}

