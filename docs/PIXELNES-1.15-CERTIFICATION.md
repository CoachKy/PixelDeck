# PixelNES 1.15 certification

PixelNES `1.15.022` is the MMC5 expansion-audio feature generation. The
feature number remains 15, while iteration 21 continues the cumulative
PixelNES implementation count with adaptive host/audio pacing and iteration
22 adds collection-wide compatibility evidence.

## Compatibility envelope

This release retains the PixelNES 1.14 contract:

- NTSC timing and two standard NES controllers.
- The same 53 mapper/submapper variants across mappers
  0/1/2/3/4/5/7/8/9/10/11/13/15/19/21/22/23/32/33/34/41/64/66/69/71/75/79/90/113/118/119/228/232/240.
- The July 25 collection snapshot of 818 local files (814 unique images)
  resolves to implemented, launchable mapper and console configurations.

The complete mapper table and legacy-header policy remain recorded in
[PixelNES 1.12 certification](PIXELNES-1.12-CERTIFICATION.md). PixelNES
1.13 and 1.14 CPU/APU/DMA behavior remains recorded in their corresponding
certification documents.

## MMC5 audio work

This generation independently implements the following MMC5 behavior:

- Two pulse channels at `$5000-$5007`, including duty, constant or envelope
  volume, phase reset, timer periods below eight, channel enable/status, and
  the MMC5 fixed 240 Hz envelope and length-counter clock.
- The 8-bit PCM DAC at `$5010-$5011` in direct-write and program-read modes.
  Reads from `$8000-$BFFF` feed the DAC only while read mode is enabled.
- A zero PCM sample preserves the previous DAC level and trips the PCM IRQ.
  `$5010` exposes the enabled IRQ state and acknowledges it on read.
- MMC5's pulse and PCM output polarity is inverted before the signal enters
  PixelNES's shared filtered expansion-audio mixer.
- Save-state format 19 preserves pulse timers, phase, envelopes, length,
  240 Hz divider phase, PCM DAC/mode, and pending PCM IRQ state.

The behavior is protected by register, timing, IRQ, mixer-source, channel
disable, and exact save-state restoration regressions.

## Debug real-time performance

The timing-critical PixelNES assembly is JIT-optimized in every configuration
while retaining its PDB debug symbols. This prevents IDE Debug launches from
falling below the NTSC presentation rate after the cycle/dot-accurate
scheduler and renderer work, without changing emulated timing or PixelDeck's
frame-pacing policy.

The synthetic worst-case 300-frame renderer gate passes in Debug, and the
local 602-frame River City Ransom mapper-4 smoke route completes comfortably
faster than real time. Release builds retain their existing headroom.

## Adaptive host/audio pacing

The frontend retains the NES's `60.0988` Hz absolute frame deadline and uses
the buffered 48 kHz audio duration as a slow secondary timing reference.
Normal-speed Windows playback targets 40 ms of core-side audio, ignores an
8 ms deadband, smooths callback jitter, and bounds host-wait correction to
0.5% in either direction. The correction changes only how long the host waits
between complete frames; CPU, PPU, APU, mapper, and sample work per frame are
unchanged.

Unavailable audio and 2X fast-forward use the exact uncorrected frame
interval. Pause resets accumulated feedback before playback resumes.

## Reference implementations

The implementation was behaviorally cross-checked against:

- [NESdev's MMC5 audio documentation](https://www.nesdev.org/wiki/MMC5_audio)
  for the externally observable register, timing, polarity, PCM, and IRQ
  contract.
- [MesenCE](https://github.com/nesdev-org/MesenCE) for an actively maintained
  pulse-timer and shared-expansion-mixer reference.
- [ares](https://github.com/ares-emulator/ares) as an independent reference
  for the fixed-rate frame unit, pulse phase, status, and PCM register paths.

No GPL implementation code was copied into PixelDeck. The references were
used to identify hardware behavior, which was independently expressed in the
existing C# mapper and mixer architecture.

## Validation evidence

Validation completed on July 25, 2026:

| Gate | Result |
| --- | ---: |
| Focused MMC5 mapper/audio regressions | 5/5 pass |
| Adaptive pacing regressions in Debug and Release | 4/4 pass |
| Deterministic Release suite excluding local ROM walks | 241/241 pass |
| River City Ransom 602-frame Debug route | Pass |
| Blargg CPU/APU/PPU and MMC3 pinned catalog | 66/66 ROMs pass |
| Windows/Linux x64/Raspberry Pi ARM64 build gates | Pass |
| 818-image, 600-frame collection routes | 730 pass, 88 review warnings |
| Collection-route failures / unsupported / invalid | 0 / 0 / 0 |
| Exact collection save-state replays / dropped audio | 818/818 / 0 |
| Slowest collection core / largest p99 frame | 304.9 FPS / 8.829 ms |

The collection runner and interpretation of its bounded evidence are
documented in
[the PixelNES compatibility laboratory guide](PIXELNES-COMPATIBILITY-LAB.md).

## Remaining limits

This is a strong compatibility build, not a cycle-perfect claim. PAL/Dendy,
VS System, PlayChoice-10, Famicom Disk System, Four Score, Zapper and other
special peripherals remain outside the supported envelope. MMC5 vertical
split rendering remains incomplete. The remaining narrow DMC caveat is the
CPU-revision-dependent single-byte sample duplication glitch.

Run the deterministic suite without local ROM walks with:

```powershell
dotnet test PixelDeck.sln -c Release --filter "FullyQualifiedName!~PixelDeck.App.Tests.NesMachineTests&FullyQualifiedName!~PixelDeck.App.Tests.SnesMachineTests"
```

Run the opt-in full local release certification with:

```powershell
./scripts/Test-PixelNesRelease.ps1
```

Run the non-stopping collection-wide compatibility audit with:

```powershell
./scripts/Test-PixelNesCompatibility.ps1
```
