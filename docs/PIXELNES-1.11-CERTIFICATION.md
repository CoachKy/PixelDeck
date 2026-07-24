# PixelNES 1.11 certification

PixelNES `1.11.015` is the Sunsoft FME-7/5B and Namco 129/163 feature
generation. The feature number advances from 10 to 11, while iteration 15
continues the cumulative PixelNES implementation count rather than resetting
it.

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
| 113 | NINA multicart variant | 0 |
| 118 | TxSROM | 0 |
| 119 | TQROM | 0 |
| 228 | Action 52 / Cheetahmen II | 0 |
| 232 | Camerica Quattro | 0 |

FME-7 implements eight 1 KiB CHR banks, four 8 KiB CPU windows, selectable
ROM/RAM at `$6000`, four mirroring modes, and its 16-bit cycle down-counter
IRQ. Sunsoft 5B audio includes three tone channels, noise, envelope shapes,
the documented logarithmic volume curve, register-write disabling, and full
save-state restoration.

Namco 129/163 implements eight pattern and four nametable CHR selectors,
shared CIRAM pattern mapping, three switchable 8 KiB PRG windows, protected
external RAM, 128 bytes of chip RAM, and the 15-bit cycle up-counter IRQ.
Namco 163 audio updates its one-to-eight wavetable channels serially every 15
CPU cycles, preserves the 24-bit phases in chip RAM, honors sound disable and
NES 2.0 mixing-volume submappers, and restores the held serial output exactly
through save states. Mapper-19 chip RAM is included in battery saves when the
cartridge declares a battery.

## July 24, 2026 collection snapshot

Every `.nes` image currently under `Games/Nintendo` was inspected:

| Measurement | Result |
| --- | ---: |
| Valid iNES/NES 2.0 images | 822 |
| Archaic iNES headers sanitized | 53 |
| Images using an implemented mapper/submapper | 818 |
| Images launchable inside the current region/console/input envelope | 817 |
| Unsupported mapper headers | 4 |
| Otherwise rejected images | 1 |

The non-mapper rejection is a PAL image, deliberately not launched with NTSC
CPU/PPU timing.

The remaining mapper headers are:

| Mapper | Local images | Work still required |
| ---: | ---: | --- |
| 8 | 1 | FFE conversion-board behavior |
| 15 | 1 | multicart banking |
| 160 | 1 | unlicensed Aladdin board behavior |
| 240 | 1 | suspect header; verify the Bird Week dump before trusting its declared board |

Mapper 240 remains unsupported because commercial Bird Week normally used a
CNROM board. A green compatibility count is not enough reason to trust a
suspect header.

## Validation evidence

Validation completed on July 24, 2026:

| Gate | Result |
| --- | --- |
| Focused mapper-19, expansion-mixer, and mapper-contract gate | 5/5 pass |
| Supported mapper/submapper variants | 49/49 synthetic images boot with bounded audio |
| Deterministic solution suite excluding opt-in local ROM soaks | 162/162 pass |
| New local mapper images | 2/2 complete the 600-frame/start-input active-frame smoke |
| New local mapper images | exact next-frame save-state restoration pass |
| Visual inspection | Batman: Return of the Joker and Splatterhouse produce recognizable title screens |

The local smoke is a boot and early-input test, not a claim that every path in
either game has been completed. The legacy Splatterhouse image is marked as
mapper 19 even though this title is also commonly associated with cost-reduced
Namco boards; its observed boot path is compatible with the implemented
mapper-19 behavior.

PAL/Dendy timing, VS System, PlayChoice-10, Famicom Disk System, Zapper and
other special peripherals, Four Score, and unimplemented cartridge expansion
audio remain explicit exclusions.

Run the deterministic suite without the local NES/SNES image walks with:

```powershell
dotnet test PixelDeck.sln -c Release --filter "FullyQualifiedName!~PixelDeck.App.Tests.NesMachineTests&FullyQualifiedName!~PixelDeck.App.Tests.SnesMachineTests"
```

Run the long, opt-in local release soak with:

```powershell
./scripts/Test-PixelNesRelease.ps1
```
