# PixelNES 1.14 certification

PixelNES `1.14.018` is the delayed DMC stop and DMA-cancellation feature
generation. The feature number advances from 13 to 14, while iteration 18
continues the cumulative PixelNES implementation count.

## Compatibility envelope

This release retains the PixelNES 1.13 contract:

- NTSC timing and two standard NES controllers.
- The same 53 mapper/submapper variants across mappers
  0/1/2/3/4/5/7/8/9/10/11/13/15/19/21/22/23/32/33/34/41/64/66/69/71/75/79/90/113/118/119/228/232/240.
- The July 24 collection snapshot of 822 valid local NES images continues to
  resolve to implemented, launchable mapper and console configurations.

The complete mapper table and legacy-header policy remain recorded in
[PixelNES 1.12 certification](PIXELNES-1.12-CERTIFICATION.md). PixelNES 1.13's
CPU/APU bus behavior remains recorded in
[PixelNES 1.13 certification](PIXELNES-1.13-CERTIFICATION.md).

## Accuracy work

This generation independently implements the following NTSC 2A03 behavior:

- Clearing DMC enable through `$4015` no longer erases the sample reader on
  the register-write edge. The stop reaches the reader after the phase-specific
  two- or three-clock delay.
- A DMC reload request accepted before that stop can still halt the CPU for
  one cycle. Once cancellation reaches the reader, PixelNES suppresses the
  remaining dummy, optional alignment, and cartridge-read cycles.
- A request delayed by CPU writes can disappear before a readable CPU cycle
  accepts the halt, matching the same state-machine behavior without a
  game-specific workaround.
- Save-state format 18 preserves the in-flight disable delay. Older
  development states are deliberately rejected rather than restored at an
  incorrect DMA phase.

These changes cover the explicit-stop DMA behavior documented from hardware.
They are protected by APU phase, CPU halt-count, IRQ, and save-state
regressions.

## Reference implementations

The implementation was behaviorally cross-checked against:

- [NESdev's DMA documentation](https://www.nesdev.org/wiki/DMA), including
  its hardware-derived explicit-stop and aborted-transfer timing.
- [MesenCE](https://github.com/nesdev-org/MesenCE), particularly the separate
  delayed DMC disable and CPU-side transfer-cancellation states.
- [ares](https://github.com/ares-emulator/ares) as an independent compact
  timing reference.

No GPL implementation code was copied into PixelDeck. The references were
used to identify observable hardware behavior, which was independently
expressed in the existing C# scheduler.

## Validation evidence

Validation completed on July 25, 2026:

| Gate | Result |
| --- | ---: |
| Deterministic solution suite excluding local ROM walks | 213/213 pass |
| Focused CPU/APU timing regressions | 21/21 pass |
| Blargg required CPU/APU/PPU baseline | 20/20 ROMs pass |
| Deep CPU/APU/PPU protocol suite | 24/24 ROMs pass |
| MMC3 Sharp/new IRQ suite | 5/5 ROMs pass |
| MMC3 NEC/old IRQ suite | 1/1 ROM passes |
| Sprite overflow/hit visual suite | 16/16 ROMs pass |
| Windows/Linux x64/Raspberry Pi ARM64 build gates | Pass |

## Remaining limits

This is a strong compatibility build, not a cycle-perfect claim. PAL/Dendy,
VS System, PlayChoice-10, Famicom Disk System, Four Score, Zapper and other
special peripherals remain outside the supported envelope. MMC5 vertical
split and expansion audio are still incomplete. The remaining narrow DMC
caveat is the CPU-revision-dependent single-byte sample duplication glitch.

Run the deterministic suite without local ROM walks with:

```powershell
dotnet test PixelDeck.sln -c Release --filter "FullyQualifiedName!~PixelDeck.App.Tests.NesMachineTests&FullyQualifiedName!~PixelDeck.App.Tests.SnesMachineTests"
```

Run the opt-in full local release certification with:

```powershell
./scripts/Test-PixelNesRelease.ps1
```
