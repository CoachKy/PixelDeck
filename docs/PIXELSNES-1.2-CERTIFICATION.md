# Historical PixelSNES 1.2 certification attempt

> This record is retained as historical test evidence. Subsequent gameplay
> validation showed that the gates below were not sufficient to establish
> broadly playable 1.0-level compatibility. The active product version was
> therefore reset to PixelSNES 0.8.009, and this document is not a current
> release certification.

This run was originally recorded as the stable release of feature generation
2. Its version format was `major.feature.build`: prerelease builds used
`0.2.xxx`, and `1.2.000` was the proposed first stable release of that feature
set. That promotion has since been withdrawn as described above.

## Supported envelope

The historical PixelSNES 1.2 run tested this hardware envelope:

- NTSC timing and 256x224 output
- standard LoROM and HiROM map modes, including their FastROM header variants
- standard ROM, ROM+RAM, and ROM+RAM+battery cartridge types
- two standard SNES controller ports at the core API
- SPC700, IPL ROM, communication ports, and all three APU timers
- eight-channel S-DSP audio at 32 kHz stereo
- general DMA and direct/indirect HDMA
- background modes 0-7, sprites, windows, mosaic, and color math
- cartridge-validated save states and durable battery-backed SRAM

The stable envelope does not include PAL timing, ExLoROM/ExHiROM, multitaps,
mouse or light-gun peripherals, or cartridge enhancement chips such as DSP-1,
Super FX, SA-1, CX4, S-DD1, or SPC7110. The dashboard rejects those images with
an explicit compatibility reason.

Modes 5 and 6 are represented at the dashboard's 256-pixel output width rather
than exposed as a separate 512-pixel high-resolution surface. PixelSNES 1.2 is
a stable, bounded compatibility release; it is not a claim of cycle-perfect
S-CPU/PPU bus timing.

## Required automated gates

Run the complete release gate from the repository root:

```powershell
./scripts/Test-PixelSnesRelease.ps1
```

The default local-game soak is 18,000 frames per supported game, approximately
five emulated minutes. A longer run can be requested explicitly:

```powershell
./scripts/Test-PixelSnesRelease.ps1 -SoakFrames 108000
```

The script pins PeterLemon's SNES CPU suite to commit
`350b394e86ec5d62f600b5cbf64cdce3721bb6ef`, downloads it into the operating
system temporary directory, and requires all 23 ROM/reference pairs. External
test ROMs and local games are never added to the repository.

The release gate requires:

1. Exact 15-bit-color agreement with all 23 published 65C816 result screens.
2. Boot tests for LoROM and HiROM ROM, RAM, and battery cartridge variants.
3. Explicit rejection tests for PAL and enhancement-chip cartridges.
4. Functional PPU tests for 8bpp Mode 3, affine Mode 7, windows, fixed color
   math, and scanline HDMA.
5. Eight-voice BRR audio, stereo, queue-overrun accounting, and exact
   S-DSP state-restoration tests.
6. Integrity-checked, transactional save states and interrupted-write recovery
   for battery SRAM.
7. The complete repository regression suite.
8. A local standard-cartridge matrix containing both LoROM and HiROM games.
9. Per-game realtime throughput, sub-frame p99 core time, bounded non-silent
   audio, no unsupported SPC700 opcodes, no dropped core samples, and exact
   mid-soak save-state restoration.
10. Framework-dependent Linux x64 and Linux ARM64 publish builds.

## Persistence guarantees

PixelSNES battery saves use write-through temporary files followed by a
same-volume replacement. A complete temporary save is recovered if a process
stops before the final rename; an incomplete or wrong-sized save cannot replace
valid cartridge RAM.

Save-state format 7 wraps a bounded payload in a SHA-256 integrity checksum.
Truncated, trailing, corrupted, wrong-game, or wrong-RAM-size states are
rejected. Loading is transactional: if validation fails after parsing begins,
the running machine is restored to its pre-load state.

## Platform status

The PixelSNES core contains no OS-specific emulation code. Windows x64 is the
locally executed release platform. Linux x64 and Linux ARM64/Raspberry Pi
artifacts are cross-published by the gate.

An ARM64 publish proves dependency resolution and native asset selection, but
not Raspberry Pi display, audio-device, controller, thermal, or frame-pacing
behavior. Raspberry Pi remains build-ready until the local-game soak is
recorded on target hardware.

## Current evidence

Certification run on July 23, 2026:

| Gate | Result |
| --- | --- |
| 65C816 reference screens | 23/23 exact |
| Standard cartridge variants | 6/6 pass |
| Unsupported PAL/enhancement contract | pass |
| PPU/HDMA focused regression | pass |
| Eight-voice S-DSP regression | pass |
| SRAM/save-state integrity | pass |
| Complete repository suite | 108/108 pass |
| Local LoROM/HiROM soak | 7 games, 126,000 frames pass |
| Local audio | non-silent for every game, zero dropped core samples |
| Windows x64 release gate | pass |
| Linux x64 publish | pass |
| Linux ARM64 publish | pass |
| Raspberry Pi on-device run | pending hardware |

The release number must not be promoted merely because the project builds. The
complete script must pass against the release source, and any platform
advertised as runtime-supported must have an execution result rather than only
a cross-publish result.
