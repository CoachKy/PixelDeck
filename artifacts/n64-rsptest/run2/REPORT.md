# Pixel64 compatibility report

- Pixel64: `0.16.026`
- Started: `2026-08-04T15:58:48.5387495+00:00`
- Completed: `2026-08-04T15:58:48.5450760+00:00`
- Games folder: `C:\GitHub\PixelDeck\tests\PixelDeck.App.Tests\TestRoms\N64\RSPTest-CP2`
- Video fields per image: `300`
- Parallel emulators: `4`
- Graphics-task captures: `False`
- Filename filter: `VADD_\\|VSUB_`

## Summary

| Total | Unique | Pass | Warning | Failed | Invalid |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 0 | 0 | 0 | 0 | 0 | 0 |

## Hardware profile coverage

| CIC | Region | Total | Pass | Warning | Failed |
| --- | --- | ---: | ---: | ---: | ---: |

## First blockers

None.

## Failures

None.

## Warnings

None.

## Interpretation

`Pass` proves only this bounded automated route; it does not certify the whole game. `Warning` highlights unverified cartridges, missing activity, unsupported HLE work, or performance below realtime. `Failed` is a runtime, CPU, audio-integrity, or exact save-state failure. The audit creates no battery-save files and never modifies ROMs. Full counters remain in `games.csv` and `report.json`.
