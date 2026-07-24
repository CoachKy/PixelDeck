# PixelNES 1.12 certification

PixelNES `1.12.016` is the legacy-multicart and J.Y. Company feature
generation. The feature number advances from 11 to 12, while iteration 16
continues the cumulative PixelNES implementation count.

## Supported envelope

The core remains NTSC-first and supports standard controllers. Its exact
mapper/submapper contract contains 53 variants:

| Mapper | Board family | Supported submappers |
| ---: | --- | --- |
| 0 | NROM | 0 |
| 1 | MMC1 | 0 |
| 2 | UxROM | 0, 1, 2 |
| 3 | CNROM | 0, 1, 2 |
| 4 | MMC3 | 0, 4 |
| 5 | MMC5 | 0 |
| 7 | AxROM | 0, 1, 2 |
| 8 | Super Magic Card mode 4 | 0 |
| 9 | MMC2 | 0 |
| 10 | MMC4 | 0 |
| 11 | Color Dreams / Wisdom Tree | 0 |
| 13 | CPROM | 0 |
| 15 | K-1029 / K-1030P multicart | 0 |
| 19 | Namco 129 / 163 | 0, 1, 2, 3, 4, 5 |
| 21 | Konami VRC4a / VRC4c | 0, 1, 2 |
| 22 | Konami VRC2a | 0 |
| 23 | Konami VRC2b / VRC4e / VRC4f | 0, 1, 2, 3 |
| 32 | Irem G-101 | 0 |
| 33 | Taito TC0190 | 0 |
| 34 | BNROM / NINA-001 | 0, 1, 2 |
| 41 | Caltron 6-in-1 | 0 |
| 64 | Tengen RAMBO-1 | 0 |
| 66 | GxROM | 0 |
| 69 | Sunsoft FME-7 / 5A / 5B | 0 |
| 71 | Camerica | 0 |
| 75 | Konami VRC1 | 0 |
| 79 | NINA-03 / NINA-06 | 0 |
| 90 | J.Y. Company ASIC | 0 |
| 113 | NINA multicart variant | 0 |
| 118 | TxSROM | 0 |
| 119 | TQROM | 0 |
| 228 | Action 52 / Cheetahmen II | 0 |
| 232 | Camerica Quattro | 0 |
| 240 | Expansion-space GNROM | 0 |

Mapper 8 implements Super Magic Card latch mode 4 with four 32 KiB PRG
banks, four 8 KiB CHR banks, expansion RAM, and CHR write protection.
Mapper 15 implements all four K-1029 PRG modes, mapper-controlled mirroring,
physical-board CHR protection, and the conventional RAM behavior expected by
the more common mapper-hack images.

Mapper 90 implements the J.Y. Company ASIC's 8/16/32 KiB PRG and
1/2/4/8 KiB CHR modes, outer banks, four mirroring modes, MMC4-like CHR
latches, eight-M2-cycle serial multiplier, accumulator, and configurable CPU-cycle, PPU-A12,
PPU-read, or CPU-write IRQ sources. Mapper-90 wiring suppresses ROM
nametables and extended mirroring as it does on the physical board.

Mapper 240 implements its expansion-space GNROM latch, including 32 KiB PRG
and 8 KiB CHR switching.

## Legacy-header policy

PixelDeck corrects a header only when its structure or the mapper
specification makes the declaration unambiguous:

- A legacy mapper-8 image with 64 KiB PRG and 64 KiB CHR cannot fit Super
  Magic Card mode 4's CHR address range. It is loaded as the matching
  NINA-06 mapper 79 layout.
- Mapper 160 is obsolete; NESdev documents mapper 90 as fully encompassing
  the behavior it attempted to describe, so these images load as mapper 90.
- A non-NES-2.0 header with non-zero old reserved bytes is treated as
  archaic. Its undefined upper mapper, console, RAM, and timing fields are
  ignored rather than accepted as hardware declarations.

The local Aladdin payload has headerless CRC32 `1306EE62`, independently
cataloged as mapper 90. The local Bird Week header contains the old `Ni0330`
dumper signature and duplicates its 16 KiB PRG half; after reserved-tail
sanitization it behaves as its intended NROM conversion.

## July 24, 2026 collection snapshot

Every `.nes` image currently under `Games/Nintendo` was inspected:

| Measurement | Result |
| --- | ---: |
| Valid iNES/NES 2.0 images | 822 |
| Archaic iNES headers sanitized | 79 |
| Images using an implemented mapper/submapper | 822 |
| Images launchable inside the current region/console/input envelope | 822 |
| Unsupported mapper headers | 0 |
| Otherwise rejected images | 0 |

This is a header and early-execution compatibility result, not a claim that
every path through all 822 games has been completed.

## Validation evidence

Validation completed on July 24, 2026:

| Gate | Result |
| --- | --- |
| Deterministic solution suite excluding local ROM walks | 170/170 pass |
| Supported mapper/submapper contract | 53/53 synthetic images boot with bounded audio |
| New mapper/header focused tests | PRG/CHR modes, mirroring, RAM, IRQ, arithmetic, and save-state restoration pass |
| Affected local images | 4/4 complete the 600-frame/start-input active-frame smoke |
| Affected local images | 4/4 exact next-frame save-state restoration pass |
| Visual inspection | Aladdin reaches gameplay; Bird Week produces its recognizable title screen |
| Build | Release solution build completes with zero warnings and zero errors |

PAL/Dendy timing, VS System, PlayChoice-10, Famicom Disk System, Zapper and
other special peripherals, Four Score, and unimplemented cartridge expansion
audio remain explicit exclusions.

Run the deterministic suite without local NES/SNES image walks with:

```powershell
dotnet test PixelDeck.sln -c Release --filter "FullyQualifiedName!~PixelDeck.App.Tests.NesMachineTests&FullyQualifiedName!~PixelDeck.App.Tests.SnesMachineTests"
```

Run the long, opt-in local release soak with:

```powershell
./scripts/Test-PixelNesRelease.ps1
```
