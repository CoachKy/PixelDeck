# PixelNES 1.9 certification

PixelNES `1.9.013` is the collection-compatibility feature generation. The
feature number advances from 8 to 9, while iteration 13 continues the
project-wide PixelNES implementation count rather than resetting it. It
retains the PixelNES 1.8 CPU, PPU, APU, scheduler, DMA, MMC5, persistence, and
accuracy baseline, and expands cartridge-board support based on a complete
inspection of the local Nintendo library.

## Supported envelope

The core remains NTSC-first and supports standard controllers. Its mapper
contract is:

| Mapper | Board family | Supported submappers |
| ---: | --- | --- |
| 0 | NROM | 0 |
| 1 | MMC1 | 0 |
| 2 | UxROM | 0, 1, 2 |
| 3 | CNROM | 0, 1, 2 |
| 4 | MMC3 | 0, 4 |
| 5 | MMC5 | 0 |
| 7 | AxROM | 0, 1, 2 |
| 9 | MMC2 | 0 |
| 10 | MMC4 | 0 |
| 11 | Color Dreams / Wisdom Tree | 0 |
| 13 | CPROM | 0 |
| 32 | Irem G-101 | 0 |
| 33 | Taito TC0190 | 0 |
| 34 | BNROM / NINA-001 | 0, 1, 2 |
| 41 | Caltron 6-in-1 | 0 |
| 66 | GxROM | 0 |
| 71 | Camerica | 0 |
| 75 | Konami VRC1 | 0 |
| 79 | NINA-03 / NINA-06 | 0 |
| 113 | NINA multicart variant | 0 |
| 118 | TxSROM | 0 |
| 119 | TQROM | 0 |
| 228 | Action 52 / Cheetahmen II | 0 |
| 232 | Camerica Quattro | 0 |

Mapper 118 includes its per-nametable CIRAM selection. Mapper 119 includes
per-bank CHR-ROM/CHR-RAM selection. MMC2/MMC4 PPU latches, board-specific PRG
windows, mirroring controls, mapper RAM, bus conflicts where present, and all
new mapper registers are included in save states.

Archaic iNES headers are sanitized according to the format's version rules:
undefined bytes 7-15 no longer invent upper mapper bits, RAM capacity, or PAL
timing. A structurally identifiable legacy Super Black Onyx conversion that
claims mapper 33, CHR RAM, battery RAM, and VS-System hardware is corrected to
its MMC1/SNROM board.

## July 24, 2026 collection audit

Every `.nes` image under `Games/Nintendo` was inspected:

| Measurement | Result |
| --- | ---: |
| Valid iNES/NES 2.0 images | 828 |
| Archaic iNES headers sanitized | 55 |
| Images using an implemented mapper/submapper | 816 |
| Images launchable inside the current region/console/input envelope | 814 |
| Unsupported mapper headers | 12 |
| Otherwise rejected images | 2 |

The two non-mapper rejections are one PAL image and one extended-console image.
They are deliberately not launched as NTSC standard-console cartridges.

The remaining mapper headers are:

| Mapper | Local images | Work still required |
| ---: | ---: | --- |
| 8 | 1 | FFE conversion-board behavior |
| 15 | 1 | multicart banking |
| 19 | 1 | Namco 163 banking, IRQ, nametables, and expansion audio |
| 21 | 1 | VRC4 address wiring and IRQ |
| 22 | 1 | VRC2 address wiring |
| 23 | 2 | VRC2/VRC4 address variants and IRQ |
| 64 | 2 | RAMBO-1 banking and scanline/cycle IRQ behavior |
| 69 | 1 | Sunsoft FME-7 banking/IRQ and 5B expansion audio |
| 160 | 1 | unlicensed Aladdin board behavior |
| 240 | 1 | suspect header; the local Bird Week image should be verified against a known-good dump |

Mapper 240 was not added merely to make the count green: the commercial Bird
Week board is normally CNROM, so that image needs header verification before
the emulator treats its declared mapper as authoritative.

## Validation evidence

Validation completed on July 24, 2026:

| Gate | Result |
| --- | --- |
| Mapper, cartridge-header, and mapper-contract tests | 48/48 pass |
| Supported mapper/submapper variants | 34/34 synthetic images boot with bounded audio |
| Complete deterministic solution suite | 154/154 pass |
| Newly supported mapper images | 71/71 complete the 600-frame/start-input smoke |
| Corrected Super Black Onyx image | completes the same active-frame smoke |
| Newly supported local mapper cohort | exact next-frame save-state restoration pass |

The collection audit establishes the supported header and board envelope. A
boot smoke is not the same as completing every commercial game. The remaining
boards, PAL/Dendy timing, VS System, PlayChoice-10, Famicom Disk System, Zapper
and other special peripherals, Four Score, and unimplemented cartridge
expansion audio remain explicit exclusions.

Run the normal deterministic suite with:

```powershell
dotnet test PixelDeck.sln
```

Run the long, opt-in local release soak with:

```powershell
./scripts/Test-PixelNesRelease.ps1
```
