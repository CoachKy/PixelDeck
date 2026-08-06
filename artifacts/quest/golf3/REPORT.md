# Pixel64 compatibility report

- Pixel64: `0.16.026`
- Started: `2026-08-04T22:04:09.8721458+00:00`
- Completed: `2026-08-04T22:04:54.9780395+00:00`
- Games folder: `C:\GitHub\PixelDeck\Games\Nintendo64`
- Video fields per image: `2400`
- Parallel emulators: `4`
- Graphics-task captures: `False`
- Filename filter: `Mario Golf`

## Summary

| Total | Unique | Pass | Warning | Failed | Invalid |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 | 0 | 1 | 0 | 0 |

## Hardware profile coverage

| CIC | Region | Total | Pass | Warning | Failed |
| --- | --- | ---: | ---: | ---: | ---: |
| Cic6102 | Ntsc | 1 | 0 | 1 | 0 |

## First blockers

None.

## Failures

None.

## Warnings

| Game | Code | Fields | PC | Finding | Capture |
| --- | --- | ---: | --- | --- | --- |
| Mario Golf (USA).n64 | NMFE | 2400 | `0x80029EB4` | Core throughput was 53.8 fields/s, below 60.0 fields/s realtime.; Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | [0000-Mario Golf (USA)-3A055916.bmp](captures/0000-Mario%20Golf%20%28USA%29-3A055916.bmp) |

## Interpretation

`Pass` proves only this bounded automated route; it does not certify the whole game. `Warning` highlights unverified cartridges, missing activity, unsupported HLE work, or performance below realtime. `Failed` is a runtime, CPU, audio-integrity, or exact save-state failure. The audit creates no battery-save files and never modifies ROMs. Full counters remain in `games.csv` and `report.json`.
