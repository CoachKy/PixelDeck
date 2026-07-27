# PixelSNES 1.15 certification

PixelSNES 1.15.020 is the first release build of the in-repository Super
Nintendo core. It supersedes the 0.15.019 development build and the withdrawn
1.2 attempt recorded in
[the historical PixelSNES 1.2 certification](PIXELSNES-1.2-CERTIFICATION.md).

The 1.0 claim rests on sustained gameplay validation across the local library
rather than on boot checks alone. Every gate listed in
[PixelSNES 0.15 certification](PIXELSNES-0.15-CERTIFICATION.md) is retained.

## Release claim

PixelSNES plays the standard Super Nintendo cartridge envelope: NTSC LoROM,
HiROM, and ExHiROM images, including FastROM header variants and standard ROM,
RAM, battery-backed RAM, DSP-1, and Capcom CX4 cartridge types. Copier-headered
and headerless images are both accepted. Gameplay, audio, battery saves, and
save states are validated on the local library rather than asserted.

## Known exclusion: Super FX

**PixelSNES 1.15.020 does not implement the Super FX (GSU-1/GSU-2)
coprocessor.** Cartridges that carry it will not run correctly. This is a
deliberate, documented exclusion rather than an undiscovered defect, and it is
the single largest gap in the release envelope. Affected titles include:

- Star Fox / Starwing
- Super Mario World 2: Yoshi's Island
- Stunt Race FX / Wild Trax
- Doom
- Winter Gold, Dirt Trax FX, Vortex

Super FX support is the primary candidate for the next PixelSNES feature.

## Retained bounded scope

The 1.0 claim is a compatibility and playability claim, not a claim of
cycle-perfect hardware emulation. The following remain outside the certified
envelope and are unchanged from 0.15:

- Cycle-perfect S-CPU/PPU, DSP-1B, and CX4 timing.
- WRAM refresh pauses, exact DMA alignment and post-write activation,
  cycle-stealing HDMA, dummy-access address speeds, and mid-instruction event
  ordering.
- Native 512-pixel high-resolution output, overscan, and interlace.
- PAL timing.
- Enhancement chips other than DSP-1 and CX4 — including Super FX above,
  SA-1, S-DD1, and the SPC7110.
- On-device Raspberry Pi validation.

## Reproducing the gates

Run `./scripts/Test-PixelSnesRelease.ps1` from the repository root.
