# PixelNES 1.8 certification

This is the retained MMC5 feature-generation record. The current supported
mapper envelope and collection audit are documented in
[PixelNES 1.9 certification](PIXELNES-1.9-CERTIFICATION.md).

PixelNES 1.8 is feature generation 8. Version `1.8.012` adds Nintendo MMC5
cartridge support to the stable PixelNES 1.7 CPU, PPU, APU, scheduler, DMA,
persistence, and mapper baseline.

## Supported envelope

PixelNES 1.8 retains the PixelNES 1.7 NTSC and standard-controller envelope and
adds mapper 5 submapper 0 for cartridges that do not require MMC5 vertical
split rendering or MMC5 expansion audio.

| Mapper | Board family | Supported submappers |
| ---: | --- | --- |
| 0 | NROM | 0 |
| 1 | MMC1 | 0 |
| 2 | UxROM | 0, 1, 2 |
| 3 | CNROM | 0, 1, 2 |
| 4 | MMC3 | 0, 4 |
| 5 | MMC5 | 0 |
| 7 | AxROM | 0, 1, 2 |
| 66 | GxROM | 0 |

The MMC5 implementation includes:

- all four PRG banking modes and ROM/RAM selection;
- protected 8 KiB-banked work RAM;
- all four CHR banking modes;
- independent background and 8x16-sprite CHR banks;
- CIRAM A, CIRAM B, ExRAM, and fill-mode nametable sources;
- extended-attribute palette and 4 KiB CHR selection;
- scanline IRQ status, enable, acknowledgement, and NMI-frame reset;
- the 8-bit hardware multiplier; and
- complete mapper and ExRAM save-state restoration.

MMC5 vertical split rendering and its two pulse/PCM expansion-audio channels
are not yet part of the supported envelope. Castlevania III (USA) does not
depend on those omitted paths during the tested title and opening sequence.

The other PixelNES 1.7 exclusions remain: PAL/Dendy timing, VS System,
PlayChoice-10, Famicom Disk System, Zapper and other special peripherals, Four
Score, other cartridge expansion audio, and mapper families not listed above.

## Automated gates

The mapper contract contains 15 supported mapper/submapper combinations. MMC5
has focused tests for:

1. all PRG modes and fixed/switchable ROM windows;
2. work-RAM write protection and restoration;
3. independent background and sprite CHR selection;
4. CIRAM, ExRAM, fill, and extended-attribute nametable behavior;
5. scanline IRQ assertion and acknowledgement;
6. multiplier low/high results; and
7. exact mapper save-state restoration.

The standard release command remains:

```powershell
./scripts/Test-PixelNesRelease.ps1
```

The local release matrix must now include mappers 1, 2, 4, 5, and 66.

## Current evidence

Implementation validation on July 23, 2026:

| Gate | Result |
| --- | --- |
| Mapper-focused regression tests | 17/17 pass |
| Supported mapper/submapper variants | 15/15 boot, video, audio |
| Core/dashboard regression suite, excluding the moving local-ROM smoke | 129/129 pass |
| Castlevania III startup | correct Konami title and opening sequence |
| Castlevania III one-minute soak | 3,600/3,600 frames pass |
| Castlevania III frame time | 7.038 ms p99 |
| Castlevania III audio | 2,875,200 bounded samples, peak 0.501 |
| Castlevania III save state | exact mid-soak next-frame restoration |

This evidence establishes the bounded MMC5 scope above. It is not a claim that
every MMC5 title is compatible, especially software that uses vertical split
or expansion audio.

The local Nintendo folder was still receiving hundreds of additional images
during this implementation. Its broad smoke test is therefore not part of the
MMC5 acceptance result. That moving matrix separately exposed blank startup
frames in Dragon Warrior III, Dragon Warrior IV, and Metal Gear; those are
pre-existing mapper 1/2 coverage gaps and are not regressions caused by mapper
5.
