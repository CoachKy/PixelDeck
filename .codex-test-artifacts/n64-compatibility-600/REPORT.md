# Pixel64 compatibility report

- Pixel64: `0.9.009`
- Started: `2026-07-28T16:51:18.4882574+00:00`
- Completed: `2026-07-28T16:51:40.5244637+00:00`
- Games folder: `C:\GitHub\PixelDeck\Games\Nintendo64`
- Video fields per image: `600`
- Parallel emulators: `4`

## Summary

| Total | Unique | Pass | Warning | Failed | Invalid |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 13 | 13 | 1 | 12 | 0 | 0 |

## Hardware profile coverage

| CIC | Region | Total | Pass | Warning | Failed |
| --- | --- | ---: | ---: | ---: | ---: |
| Cic6102 | Ntsc | 10 | 1 | 9 | 0 |
| Cic6103 | Ntsc | 1 | 0 | 1 | 0 |
| Cic6105 | Ntsc | 2 | 0 | 2 | 0 |

## First blockers

None.

## Failures

None.

## Warnings

| Game | Code | Fields | PC | Finding | Capture |
| --- | --- | ---: | --- | --- | --- |
| Donkey Kong 64 (USA).z64 | NDOE | 600 | `0x80000A08` | No graphics RSP task was submitted during the audit window.; No active multicolor video appeared at a checkpoint.; Checkpoint frames remained visually static.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | - |
| GoldenEye 007 (USA).z64 | NGEE | 600 | `0x7000071C` | The audio HLE skipped 350 command(s): 0x0E=350; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | - |
| Legend of Zelda, The - Ocarina of Time (U) (V1.2) [!].z64 | CZLE | 600 | `0x80000814` | Unsupported texture formats were configured: format-4/size-2=9907, format-0/size-0=4413, format-2/size-2=4202; The audio HLE skipped 45713 command(s): 0x14=15370, 0x15=9314, 0x12=6899, 0x13=6899, 0x16=6899, 0x11=332; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | - |
| Major League Baseball featuring Ken Griffey Jr. (USA).n64 | NKGE | 600 | `0x80025F5C` | The cartridge entry point was not reached during the audit window.; No graphics RSP task was submitted during the audit window.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | - |
| Mario Golf (USA).n64 | NMFE | 600 | `0x800001CC` | The graphics HLE skipped 4 command(s): 0xEA=1, 0xEB=1, 0xEC=1, 0xEE=1; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | - |
| Mario Kart 64 (USA).n64 | NKTE | 600 | `0x800005B8` | The graphics HLE skipped 7945 command(s): 0xB5=7212, 0xC0=733; The audio HLE skipped 72197 command(s): 0x14=19708, 0x12=15422, 0x13=15422, 0x16=15422, 0x15=5283, 0x11=940; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | - |
| Mario Tennis (USA).n64 | NM8E | 600 | `0x800001CC` | The graphics HLE skipped 4 command(s): 0xEA=1, 0xEB=1, 0xEC=1, 0xEE=1; Unsupported texture formats were configured: format-0/size-0=10; The audio HLE skipped 367 command(s): 0x0E=367; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | - |
| Pilotwings 64 (USA).n64 | NPWE | 600 | `0x8022EA5C` | Unsupported texture formats were configured: format-4/size-2=3041; The audio HLE skipped 5121 command(s): 0x0E=5121; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | - |
| Quest 64 (USA).n64 | NETE | 600 | `0x80000518` | Unsupported texture formats were configured: format-0/size-0=847; The audio HLE skipped 1713 command(s): 0x0E=1713; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | - |
| Star Wars - Rogue Squadron (USA).n64 | NRSE | 600 | `0x80001804` | The graphics HLE skipped 36694 command(s): 0x80=6175, 0xB5=3817, 0xBE=1369, 0x22=616, 0x2E=375, 0x2A=323, 0x1E=295, 0x16=222, 0x26=180, 0x12=150, 0x36=140, 0x0E=133, 0x02=80, 0x3A=53, 0x32=18; Unsupported texture formats were configured: format-4/size-2=2505; No active multicolor video appeared at a checkpoint.; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | - |
| Star Wars - Shadows of the Empire (USA) (Rev B).n64 | NSWE | 600 | `0x800C28A0` | The graphics HLE skipped 936 command(s): 0xC0=758, 0xBE=178; Unsupported texture formats were configured: format-2/size-2=7118, format-4/size-2=810, format-0/size-0=660; The audio HLE skipped 675 command(s): 0x0E=675; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | - |
| WWF WrestleMania 2000 (USA).n64 | NWXE | 600 | `0x800001CC` | Unsupported texture formats were configured: format-0/size-0=702, format-2/size-2=511; The audio HLE skipped 1171 command(s): 0x0E=1171; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | - |

## Interpretation

`Pass` proves only this bounded automated route; it does not certify the whole game. `Warning` highlights unverified cartridges, missing activity, unsupported HLE work, or performance below realtime. `Failed` is a runtime, CPU, audio-integrity, or exact save-state failure. The audit creates no battery-save files and never modifies ROMs. Full counters remain in `games.csv` and `report.json`.
