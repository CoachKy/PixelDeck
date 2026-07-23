# PixelNES 1.7 certification

PixelNES 1.7 is the stable release of feature generation 7. The version format
is `major.feature.build`: `0.7.010` is prerelease feature generation 7 build 10,
and `1.7.000` is the first stable release of that same feature set.

## Supported envelope

PixelNES 1.7 intentionally certifies this hardware envelope:

- NTSC NES timing and the RP2C02G PPU by default
- optional early RP2C02B-or-older PPU behavior
- both standard controller ports at the core API
- the five internal 2A03 APU channels at 48 kHz mono
- iNES and supported NES 2.0 cartridge metadata
- battery-backed PRG RAM and cartridge-validated save states
- mappers and submappers listed below

| Mapper | Board family | Supported submappers |
| ---: | --- | --- |
| 0 | NROM | 0 |
| 1 | MMC1 | 0 |
| 2 | UxROM | 0, 1, 2 |
| 3 | CNROM | 0, 1, 2 |
| 4 | MMC3 | 0, 4 |
| 7 | AxROM | 0, 1, 2 |
| 66 | GxROM | 0 |

Submapper 1 disables bus conflicts and submapper 2 enables them for the
applicable discrete-logic boards. Mapper 4 submapper 0 selects the Sharp/new
IRQ behavior and submapper 4 selects NEC/old behavior. Legacy iNES mapper 4
images remain user-overridable because their headers cannot identify the IRQ
chip.

The release does not claim PAL or Dendy timing, VS System, PlayChoice-10,
Zapper or other special peripherals, Four Score, cartridge expansion audio,
or unsupported mapper variants. Those are future feature releases, not hidden
partial support in 1.7.

## Required automated gates

Run the complete release gate from the repository root:

```powershell
./scripts/Test-PixelNesRelease.ps1
```

The default local-game soak is 18,000 frames per game, approximately five
emulated minutes. A longer run can be requested explicitly:

```powershell
./scripts/Test-PixelNesRelease.ps1 -SoakFrames 108000
```

The script pins the external test catalog to commit
`95d8f621ae55cee0d09b91519a8989ae0e64753b`, downloads it into the operating
system temporary directory, and fails if any expected suite has the wrong ROM
count. Test ROMs and local games are never added to the repository.

The gate requires:

1. The 20-ROM instruction/APU/vblank-NMI baseline.
2. The 24-ROM deep instruction timing, dummy access, interrupt, reset, APU
   reset, PPU open-bus/read-buffer, and OAM-read suite.
3. Five MMC3 Sharp/new IRQ tests and the separate NEC/old test.
4. All five sprite-overflow and all eleven sprite-zero-hit ROMs.
5. Exact support-contract and boot/audio checks for all 14 supported
   mapper/submapper combinations.
6. The complete repository regression suite.
7. The local ten-game mapper matrix, including mappers 1, 2, 4, and 66.
8. Per-game realtime throughput, sub-frame p99 core time, continuous bounded
   audio, zero dropped core samples, and exact mid-soak save-state restore.
9. Framework-dependent Linux x64 and Linux ARM64 publish builds.

## Persistence guarantees

PixelNES 1.7 battery saves use write-through temporary files followed by
same-volume replacement. A complete temporary save is recovered if a process
stops before the final rename; an incomplete temporary file cannot replace a
valid committed save.

Save-state format 16 wraps the state payload in a bounded length and SHA-256
integrity checksum. Invalid, truncated, trailing, wrong-game, or
wrong-configuration states are rejected. Loading is transactional: if semantic
validation fails after parsing begins, the running machine is restored to its
pre-load state.

## Platform status

The PixelNES core contains no OS-specific emulation code. Windows x64 is the
locally executed release platform. Linux x64 and Linux ARM64/Raspberry Pi
artifacts are cross-published by the gate.

An ARM64 publish proves dependency resolution and native asset selection, but
it does not prove Raspberry Pi display, audio-device, controller, thermal, or
frame-pacing behavior. Stable Raspberry Pi support therefore requires one
final run of the local-game soak on the target Pi. Until that hardware run is
recorded, Raspberry Pi is build-ready rather than runtime-certified.

## Current evidence

Certification run on July 23, 2026:

| Gate | Result |
| --- | --- |
| Required baseline | 20/20 pass |
| Deep CPU/APU/PPU suite | 24/24 pass |
| MMC3 IRQ suite | 6/6 pass |
| Sprite overflow/hit suite | 16/16 pass |
| Supported mapper/submapper variants | 14/14 boot, video, audio |
| Local mapper families represented | 1, 2, 4, 66 |
| Complete repository suite | 88/88 pass |
| Local game soak | 10/10 games, 180,000 frames pass |
| Local audio | non-silent for every game, zero dropped core samples |
| Windows x64 release gate | pass |
| Linux x64 publish | pass |
| Linux ARM64 publish | pass |
| Raspberry Pi on-device run | pending hardware |

The release number must not be promoted to `1.7.000` merely because the project
builds. The complete script must pass against the final source state, and any
platform advertised as runtime-supported must have an execution result rather
than only a cross-publish result.
