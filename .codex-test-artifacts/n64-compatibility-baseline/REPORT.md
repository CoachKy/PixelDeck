# Pixel64 compatibility report

- Pixel64: `0.9.009`
- Started: `2026-07-28T16:50:47.2872926+00:00`
- Completed: `2026-07-28T16:50:52.2236537+00:00`
- Games folder: `C:\GitHub\PixelDeck\Games\Nintendo64`
- Video fields per image: `120`
- Parallel emulators: `4`

## Summary

| Total | Unique | Pass | Warning | Failed | Invalid |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 13 | 13 | 0 | 13 | 0 | 0 |

## Hardware profile coverage

| CIC | Region | Total | Pass | Warning | Failed |
| --- | --- | ---: | ---: | ---: | ---: |
| Cic6102 | Ntsc | 10 | 0 | 10 | 0 |
| Cic6103 | Ntsc | 1 | 0 | 1 | 0 |
| Cic6105 | Ntsc | 2 | 0 | 2 | 0 |

## First blockers

None.

## Failures

None.

## Warnings

| Game | Code | Fields | PC | Finding | Capture |
| --- | --- | ---: | --- | --- | --- |
| Donkey Kong 64 (USA).z64 | NDOE | 120 | `0x80000A08` | No graphics RSP task was submitted during the audit window.; No active multicolor video appeared at a checkpoint.; Checkpoint frames remained visually static.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | [0000-Donkey Kong 64 (USA)-B6347D9F.bmp](captures/0000-Donkey%20Kong%2064%20%28USA%29-B6347D9F.bmp) |
| GoldenEye 007 (USA).z64 | NGEE | 120 | `0x7000071C` | The audio HLE skipped 375 command(s): 0x0E=375; Core throughput was 52.6 fields/s, below 60.0 fields/s realtime.; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | [0001-GoldenEye 007 (USA)-2CDCEC8A.bmp](captures/0001-GoldenEye%20007%20%28USA%29-2CDCEC8A.bmp) |
| Legend of Zelda, The - Ocarina of Time (U) (V1.2) [!].z64 | CZLE | 120 | `0x80000810` | Unsupported texture formats were configured: format-4/size-2=1656, format-0/size-0=184, format-2/size-2=92; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | [0002-Legend of Zelda, The - Ocarina of Time (U) (V1.2) [!]-49ACD388.bmp](captures/0002-Legend%20of%20Zelda,%20The%20-%20Ocarina%20of%20Time%20%28U%29%20%28V1.2%29%20[!]-49ACD388.bmp) |
| Major League Baseball featuring Ken Griffey Jr. (USA).n64 | NKGE | 120 | `0x80025F5C` | The cartridge entry point was not reached during the audit window.; No graphics RSP task was submitted during the audit window.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | [0003-Major League Baseball featuring Ken Griffey Jr. (USA)-A241344C.bmp](captures/0003-Major%20League%20Baseball%20featuring%20Ken%20Griffey%20Jr.%20%28USA%29-A241344C.bmp) |
| Mario Golf (USA).n64 | NMFE | 120 | `0x80029EB8` | The graphics HLE skipped 4 command(s): 0xEA=1, 0xEB=1, 0xEC=1, 0xEE=1; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | [0004-Mario Golf (USA)-3A055916.bmp](captures/0004-Mario%20Golf%20%28USA%29-3A055916.bmp) |
| Mario Kart 64 (USA).n64 | NKTE | 120 | `0x800005BC` | The graphics HLE skipped 1 command(s): 0xC0=1; The audio HLE skipped 1647 command(s): 0x15=981, 0x14=666; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | [0005-Mario Kart 64 (USA)-A8588FFD.bmp](captures/0005-Mario%20Kart%2064%20%28USA%29-A8588FFD.bmp) |
| Mario Tennis (USA).n64 | NM8E | 120 | `0x80031D9C` | The graphics HLE skipped 4 command(s): 0xEA=1, 0xEB=1, 0xEC=1, 0xEE=1; The audio HLE skipped 259 command(s): 0x0E=259; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | [0006-Mario Tennis (USA)-D43C0337.bmp](captures/0006-Mario%20Tennis%20%28USA%29-D43C0337.bmp) |
| Pilotwings 64 (USA).n64 | NPWE | 120 | `0x8022EA60` | The audio HLE skipped 801 command(s): 0x0E=801; No graphics RSP task was submitted during the audit window.; No active multicolor video appeared at a checkpoint.; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | [0007-Pilotwings 64 (USA)-CE2AEC86.bmp](captures/0007-Pilotwings%2064%20%28USA%29-CE2AEC86.bmp) |
| Quest 64 (USA).n64 | NETE | 120 | `0x80000514` | Unsupported texture formats were configured: format-0/size-0=324; The audio HLE skipped 273 command(s): 0x0E=273; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | [0008-Quest 64 (USA)-44420B60.bmp](captures/0008-Quest%2064%20%28USA%29-44420B60.bmp) |
| Star Wars - Rogue Squadron (USA).n64 | NRSE | 120 | `0x80001808` | The graphics HLE skipped 99 command(s): 0x02=66, 0x80=33; No active multicolor video appeared at a checkpoint.; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | [0009-Star Wars - Rogue Squadron (USA)-65472B65.bmp](captures/0009-Star%20Wars%20-%20Rogue%20Squadron%20%28USA%29-65472B65.bmp) |
| Star Wars - Shadows of the Empire (USA) (Rev B).n64 | NSWE | 120 | `0x80000FE4` | No graphics RSP task was submitted during the audit window.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | [0010-Star Wars - Shadows of the Empire (USA) (Rev B)-E69CA01A.bmp](captures/0010-Star%20Wars%20-%20Shadows%20of%20the%20Empire%20%28USA%29%20%28Rev%20B%29-E69CA01A.bmp) |
| Super Mario 64 (USA).z64 | NSME | 120 | `0x80246DDC` | No graphics RSP task was submitted during the audit window.; Audio tasks ran but produced no audible output. | [0011-Super Mario 64 (USA)-17CE0773.bmp](captures/0011-Super%20Mario%2064%20%28USA%29-17CE0773.bmp) |
| WWF WrestleMania 2000 (USA).n64 | NWXE | 120 | `0x80031290` | Unsupported texture formats were configured: format-0/size-0=79, format-2/size-2=70; The audio HLE skipped 292 command(s): 0x0E=292; Audio tasks ran but produced no audible output.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | [0012-WWF WrestleMania 2000 (USA)-618D2D15.bmp](captures/0012-WWF%20WrestleMania%202000%20%28USA%29-618D2D15.bmp) |

## Interpretation

`Pass` proves only this bounded automated route; it does not certify the whole game. `Warning` highlights unverified cartridges, missing activity, unsupported HLE work, or performance below realtime. `Failed` is a runtime, CPU, audio-integrity, or exact save-state failure. The audit creates no battery-save files and never modifies ROMs. Full counters remain in `games.csv` and `report.json`.
