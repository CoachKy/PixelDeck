# Pixel64 compatibility report

- Pixel64: `0.16.026`
- Started: `2026-08-04T21:29:59.4683086+00:00`
- Completed: `2026-08-04T21:30:09.8032955+00:00`
- Games folder: `C:\GitHub\PixelDeck\Games\Nintendo64`
- Video fields per image: `1200`
- Parallel emulators: `4`
- Graphics-task captures: `False`
- Filename filter: `Mario Kart`

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
| Mario Kart 64 (USA).n64 | NKTE | 1200 | `0x80040ADC` | Pixel64 boot attempt enabled for this Nintendo 64 cartridge. Compatibility has not yet been verified. | [0000-Mario Kart 64 (USA)-A8588FFD.bmp](captures/0000-Mario%20Kart%2064%20%28USA%29-A8588FFD.bmp) |

## Interpretation

`Pass` proves only this bounded automated route; it does not certify the whole game. `Warning` highlights unverified cartridges, missing activity, unsupported HLE work, or performance below realtime. `Failed` is a runtime, CPU, audio-integrity, or exact save-state failure. The audit creates no battery-save files and never modifies ROMs. Full counters remain in `games.csv` and `report.json`.
