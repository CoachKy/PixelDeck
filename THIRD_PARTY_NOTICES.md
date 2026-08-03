# Third-party notices

## Dolphin Emulator

PixelCube, PixelDeck's GameCube core, is derived in part from the Dolphin
Emulator. Register layouts, hardware behaviour, timing constants and protocol
implementations were taken from Dolphin's source, including the audio
interface's sample rate handling, the DSP microcode boot and mailbox protocols,
the blitting and transform processor register maps, and the command processor's
vertex formats.

Copyright (c) 2003-2026 Dolphin Emulator Project

Dolphin is licensed under the GNU General Public License, version 2 or later.
PixelDeck is therefore distributed under the same terms; see `LICENSE`.

    https://github.com/dolphin-emu/dolphin


## paraLLEl-RDP

Pixel64 optionally uses the standalone paraLLEl-RDP Vulkan compute renderer
through PixelDeck's native C ABI bridge. The source dependency is pinned to
standalone revision `388d70f5835b352d841d9d9e5a08c5de01470f41`, containing
paraLLEl-RDP core revision `1cecd042b2619bc505c12bfdc713808386f2b54d`.

Copyright (c) 2020 Themaister

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## ares

PixelDeck's Super Nintendo audio implementation was developed with reference to
the S-DSP implementation in the [ares emulator](https://github.com/ares-emulator/ares).

Copyright (c) 2004-2025 ares team, Near et al

Permission to use, copy, modify, and/or distribute this software for any
purpose with or without fee is hereby granted, provided that the above
copyright notice and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES
WITH REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF
MERCHANTABILITY AND FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR
ANY SPECIAL, DIRECT, INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES
WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN
ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF
OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THIS SOFTWARE.

## SDL and SDL3-CS

PixelDeck uses SDL and the SDL3-CS bindings for cross-platform gamepad input.

Copyright (C) 1997-2026 Sam Lantinga <slouken@libsdl.org>
Copyright (c) 2024-2026 Eduard Gushchin

This software is provided 'as-is', without any express or implied warranty.
In no event will the authors be held liable for any damages arising from the
use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not claim
   that you wrote the original software. If you use this software in a
   product, an acknowledgment in the product documentation would be
   appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
   misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.
