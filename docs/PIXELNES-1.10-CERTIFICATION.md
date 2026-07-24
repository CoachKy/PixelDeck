# PixelNES 1.10 certification

PixelNES `1.10.014` is the RAMBO-1 and VRC2/VRC4 feature generation. The
feature number advances from 9 to 10, while iteration 14 continues the
cumulative PixelNES implementation count rather than resetting it.

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
| 21 | Konami VRC4a / VRC4c | 0, 1, 2 |
| 22 | Konami VRC2a | 0 |
| 23 | Konami VRC2b / VRC4e / VRC4f | 0, 1, 2, 3 |
| 32 | Irem G-101 | 0 |
| 33 | Taito TC0190 | 0 |
| 34 | BNROM / NINA-001 | 0, 1, 2 |
| 41 | Caltron 6-in-1 | 0 |
| 64 | Tengen RAMBO-1 | 0 |
| 66 | GxROM | 0 |
| 71 | Camerica | 0 |
| 75 | Konami VRC1 | 0 |
| 79 | NINA-03 / NINA-06 | 0 |
| 113 | NINA multicart variant | 0 |
| 118 | TxSROM | 0 |
| 119 | TQROM | 0 |
| 228 | Action 52 / Cheetahmen II | 0 |
| 232 | Camerica Quattro | 0 |

VRC2/VRC4 implements the declared address-line permutations, 8 KiB PRG and
1 KiB CHR banking, VRC2a's shifted CHR wiring, VRC2's one-bit latch, VRC4
WRAM and PRG swap control, four mirroring modes where supported, and VRC4's
cycle/scanline IRQ. RAMBO-1 implements its three switchable 8 KiB PRG
windows, extended 1 KiB/2 KiB CHR arrangements, mirroring, filtered PPU-A12
scanline IRQ, CPU-cycle IRQ, delayed IRQ assertion, and mapper save state.

RAMBO-1's published hardware notes still describe uncertainty around the
counter's extra reload kick and a known tradeoff between two Skull &
Crossbones display cases. The implementation follows the behavior needed by
Klax and the published delayed-IRQ model; this certification does not claim
that unresolved analog behavior is cycle-perfect.

## July 24, 2026 collection snapshot

Every `.nes` image currently under `Games/Nintendo` was inspected:

| Measurement | Result |
| --- | ---: |
| Valid iNES/NES 2.0 images | 822 |
| Archaic iNES headers sanitized | 53 |
| Images using an implemented mapper/submapper | 816 |
| Images launchable inside the current region/console/input envelope | 815 |
| Unsupported mapper headers | 6 |
| Otherwise rejected images | 1 |

The non-mapper rejection is a PAL image, deliberately not launched with NTSC
CPU/PPU timing.

The remaining mapper headers are:

| Mapper | Local images | Work still required |
| ---: | ---: | --- |
| 8 | 1 | FFE conversion-board behavior |
| 15 | 1 | multicart banking |
| 19 | 1 | Namco 163 banking, IRQ, nametables, and expansion audio |
| 69 | 1 | Sunsoft FME-7 banking/IRQ and 5B expansion audio |
| 160 | 1 | unlicensed Aladdin board behavior |
| 240 | 1 | suspect header; verify the Bird Week dump before trusting its declared board |

Mapper 240 remains unsupported because commercial Bird Week normally used a
CNROM board. A green compatibility count is not enough reason to trust a
suspect header.

## Validation evidence

Validation completed on July 24, 2026:

| Gate | Result |
| --- | --- |
| Focused RAMBO-1/VRC mapper and contract gate | 12/12 pass |
| Supported mapper/submapper variants | 42/42 synthetic images boot with bounded audio |
| Deterministic solution suite excluding opt-in local ROM soaks | 156/156 pass |
| Newly supported local images | 6/6 complete the 600-frame/start-input active-frame smoke |
| Newly supported local mapper cohort | exact next-frame save-state restoration pass |
| VRC visual inspection | 4/4 produce recognizable game screens |

The local smoke cohort is Klax, Shinobi, Getsu Fūma Den, Gryzor, TwinBee 3,
and Wai Wai World 2. A boot smoke is not the same as completing every game.
PAL/Dendy timing, VS System, PlayChoice-10, Famicom Disk System, Zapper and
other special peripherals, Four Score, and unimplemented cartridge
expansion audio remain explicit exclusions.

Run the deterministic suite without the local NES/SNES image walks with:

```powershell
dotnet test PixelDeck.sln -c Release --filter "FullyQualifiedName!~PixelDeck.App.Tests.NesMachineTests&FullyQualifiedName!~PixelDeck.App.Tests.SnesMachineTests"
```

Run the long, opt-in local release soak with:

```powershell
./scripts/Test-PixelNesRelease.ps1
```
