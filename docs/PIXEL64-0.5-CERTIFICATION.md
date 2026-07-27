# Pixel64 0.5 certification

Pixel64 0.5.006 adds audio, demand-paged memory, and per-cartridge video
resolution to the Nintendo 64 core. It is still **not** a graphics-accurate or
generally compatible Nintendo 64 release. It supersedes
[Pixel64 0.4 certification](PIXEL64-0.4-CERTIFICATION.md).

- Product versions must report Pixel64 `0.5.006` and PixelDeck `1.18.060`.

## New in 0.5

- **Audio.** The audio microcode command list (ABI 1, `aspMain`) is emulated in
  high level: VADPCM decode, resampling, envelope mixing, and interleave. Frames
  captured from completed audio-interface DMAs are published to the app as
  32 kHz stereo. Super Mario 64 produces audible output for the first time.
- **TLB refill and invalid exceptions.** Loads, stores, and instruction fetches
  translate through the TLB. A miss vectors to `0x80000000`; a matched entry
  with the valid bit clear vectors to the general handler at `0x80000180`. CP0
  `Random` now cycles through `[Wired, 31]`.
- **Boot memory size.** IPL3 cannot probe the RDRAM controller registers Pixel64
  does not model, so `osMemSize` is published as 8 MiB when control transfers to
  the cartridge.
- **Video resolution follows the video interface.** `VI_WIDTH` is the frame
  buffer stride and the visible height is the active scan window scaled by
  `VI_Y_SCALE`. Verified: Super Mario 64, Quest 64, and Ocarina of Time program
  320x237; GoldenEye 007 programs 440x325. Save-state format 7.
- **Display list coverage.** `G_TRI4`, `G_SETSCISSOR`, `G_SETOTHERMODE_H`,
  `G_LOADTLUT`, CI4/CI8 palette textures, IA4 textures, and `G_LIGHTING`
  diffuse shading.

## Cartridge envelope

Unchanged from 0.4: every structurally valid `.z64`, `.v64`, and `.n64` image is
offered as a launchable `PARTIAL` entry, all three byte orders are normalized in
memory, and the five known CIC boot codes select their matching startup seed.

## Verified route

Super Mario 64 (USA) revision 0 (`NSME`, CIC-6102) remains the only verified
gameplay route, and its graphics output remains partial — see the known defect
below.

GoldenEye 007 (`NGEE`) is newly promoted from "boots blind" to **boots and
renders**: it demand-pages through the TLB, submits graphics and audio tasks,
polls controllers, and draws its intro geometry with zero unsupported CPU
instructions and zero unsupported display-list commands. It is not a verified
gameplay route.

## Known defects and exclusions

- **Open defect: Super Mario 64 title logo renders as a 4x3 tiled grid.** The
  logo is drawn with a 32x32 wrapping tile; framebuffer stride mismatch and
  TMEM `Line` row striding have both been measured and ruled out.
- **Only the Fast3D microcode is emulated.** F3DEX, F3DEX2, and F3DZEX are not,
  so Donkey Kong 64, WWF WrestleMania 2000, Quest 64, and Ocarina of Time
  cannot draw.
- **Only 512-byte EEPROM saves.** SRAM, FlashRAM, and Controller Pak are absent,
  so Ocarina of Time cannot save.
- No color combiner: texture and shade are multiplied directly.
- The RSP is emulated at high level only; there is no RSP or RDP low-level mode.
- 1 CPU tick per instruction; no per-instruction-class cycle costs, and RSP
  tasks complete instantly.
- No conformance-ROM harness yet.

## Reproducing the gates

Run the N64 tests with `dotnet test --filter "FullyQualifiedName~N64"`. Set
`PIXEL64_TRACE_CART=<file name fragment>` to run the per-cartridge boot
diagnostic, which reports instruction counts, task counts, video-interface
registers, and unsupported-command histograms for any local cartridge.
