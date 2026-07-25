# PixelNES 1.13 certification

PixelNES `1.13.017` is the CPU/APU data-bus and DMC-phase feature generation.
The feature number advances from 12 to 13, while iteration 17 continues the
cumulative PixelNES implementation count.

## Compatibility envelope

This release retains the PixelNES 1.12 contract:

- NTSC timing and standard NES controllers.
- The same 53 mapper/submapper variants across mappers
  0/1/2/3/4/5/7/8/9/10/11/13/15/19/21/22/23/32/33/34/41/64/66/69/71/75/79/90/113/118/119/228/232/240.
- The July 24 collection snapshot of 822 valid local NES images continues to
  resolve to implemented, launchable mapper and console configurations.

The complete mapper table and legacy-header policy remain recorded in
[PixelNES 1.12 certification](PIXELNES-1.12-CERTIFICATION.md).

## Accuracy work

This generation independently implements the following hardware behavior:

- Reads from unused `$4000-$4014` and `$4018-$401F` internal I/O addresses
  return the existing CPU external data-bus latch instead of zero.
- Standard controller ports drive bit 0, ground bits 1-4, and preserve the
  external-bus values on undriven bits 5-7.
- `$4015` preserves open-bus bit 5 and does not replace the external bus latch
  with its internally driven APU status value.
- Enabling an empty DMC channel schedules its initial DMA request after two or
  three CPU clocks according to the current APU phase.
- When DMA halts a CPU read in `$4000-$401F`, the internal APU/controller
  register selected by the DMA address's low five bits can be read
  simultaneously with the external cartridge address.
- Continuous controller-port output-enable behavior avoids deleting an
  additional input bit when two internal DMA selections are electrically one
  uninterrupted read.

Save-state format 17 preserves the in-flight DMC startup delay. Older
development states are deliberately rejected rather than restored at the
wrong DMA phase.

## Reference implementations

The implementation was behaviorally cross-checked against:

- [Mesen2](https://github.com/SourMesen/Mesen2), particularly its separate
  internal/external open-bus handling and detailed NTSC DMA arbitration.
- [ares](https://github.com/ares-emulator/ares), whose compact Famicom core
  independently models controller open-bus pins, `$4015` bit 5, and phased DMC
  startup.
- [puNES](https://github.com/punesemu/puNES) and
  [Nestopia UE](https://github.com/0ldsk00l/nestopia) as broader compatibility,
  mapper, and regression references.

No GPL implementation code was copied into PixelDeck. The sources were used
to identify hardware behavior, which was then expressed in the existing C#
core and protected by PixelNES-specific regressions.

## Validation evidence

Validation completed on July 24, 2026:

| Gate | Result |
| --- | ---: |
| Deterministic solution suite excluding local ROM walks | 195/195 pass |
| CPU/APU/bus focused regressions | 38/38 pass |
| Blargg required CPU/APU/PPU baseline | 20/20 ROMs pass |
| Deep CPU/APU/PPU protocol suite | 24/24 ROMs pass |
| MMC3 Sharp/new IRQ suite | 5/5 ROMs pass |
| MMC3 NEC/old IRQ suite | 1/1 ROM passes |
| Sprite overflow/hit visual suite | 16/16 ROMs pass |
| Build | Release build completes with zero warnings and zero errors |

The previously omitted `cpu_dummy_reads` visual ROM was also inspected and
reports `Passed`. The DMC phase and simultaneous-bus behavior have direct,
deterministic unit regressions because several older DMC diagnostic ROMs use
screen or serial protocols that are not compatible with PixelDeck's automated
`$6000` Blargg-result reader.

## Remaining limits

This is a strong compatibility build, not a cycle-perfect claim. PAL/Dendy,
VS System, PlayChoice-10, Famicom Disk System, Four Score, Zapper and other
special peripherals remain outside the supported envelope. MMC5 vertical
split and expansion audio are still incomplete. Further transistor-edge DMC
work includes delayed disable/cancellation and the revision-dependent
single-byte sample duplication glitch.

Run the deterministic suite without local ROM walks with:

```powershell
dotnet test PixelDeck.sln -c Release --filter "FullyQualifiedName!~PixelDeck.App.Tests.NesMachineTests&FullyQualifiedName!~PixelDeck.App.Tests.SnesMachineTests"
```

Run the opt-in full local release certification with:

```powershell
./scripts/Test-PixelNesRelease.ps1
```
