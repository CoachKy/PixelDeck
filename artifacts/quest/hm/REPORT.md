# Pixel64 compatibility report

- Pixel64: `0.16.026`
- Started: `2026-08-05T01:27:12.3461129+00:00`
- Completed: `2026-08-05T01:27:33.4543085+00:00`
- Games folder: `C:\GitHub\PixelDeck\Games\Nintendo64`
- Video fields per image: `1500`
- Parallel emulators: `4`
- Graphics-task captures: `False`
- Filename filter: `Harvest`

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
| Harvest Moon 64 (USA).n64 | NYWE | 1500 | `0x80026050` | Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | [0000-Harvest Moon 64 (USA)-83FAF274.bmp](captures/0000-Harvest%20Moon%2064%20%28USA%29-83FAF274.bmp) |

## Interpretation

`Pass` proves only this bounded automated route; it does not certify the whole game. `Warning` highlights unverified cartridges, missing activity, unsupported HLE work, or performance below realtime. `Failed` is a runtime, CPU, audio-integrity, or exact save-state failure. The audit creates no battery-save files and never modifies ROMs. Full counters remain in `games.csv` and `report.json`.
