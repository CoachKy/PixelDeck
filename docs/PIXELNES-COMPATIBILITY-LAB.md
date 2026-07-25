# PixelNES compatibility laboratory

PixelNES includes a local, collection-wide compatibility runner. It never
modifies cartridge images and never contacts a network service. Each run
writes timestamped evidence beneath `artifacts/nes-compatibility`, which is
ignored by Git because reports contain local filenames.

Run the default ten-second route for every NES image with:

```powershell
./scripts/Test-PixelNesCompatibility.ps1
```

Useful variants are:

```powershell
# Fast inventory: header, mapper, load, execution, timing and state safety.
./scripts/Test-PixelNesCompatibility.ps1 -FramesPerGame 120 -NoCaptures

# Investigate one title and preserve a frame when it is flagged.
./scripts/Test-PixelNesCompatibility.ps1 -FramesPerGame 3600 -Filter "ZELDA2"

# Make failed or invalid images fail the command for automation.
./scripts/Test-PixelNesCompatibility.ps1 -Strict
```

## What one route checks

For every supported image, the runner:

- inspects the iNES/NES 2.0 header and records mapper, submapper and timing;
- hashes the complete image so duplicate dumps can be identified;
- creates an isolated `NesMachine` and executes deterministic controller input;
- measures core-only frame throughput and 99th-percentile frame duration;
- verifies that CPU cycles advance and video checkpoints are produced;
- drains every audio sample and rejects non-finite or dropped samples;
- saves state midway through the route, runs one frame, restores, and requires
  the exact next frame, CPU position and audio sequence to repeat;
- continues after an individual image fails so one run describes the complete
  collection; and
- writes a BMP of the last checkpoint for warnings and failures unless
  `-NoCaptures` is selected.

The output folder contains `REPORT.md`, `games.csv`, `report.json`, and an
optional `captures` directory.

## Result meanings

- `Pass` means the image completed this bounded automated route.
- `Warning` means execution completed but an observation such as silence,
  static video, limited peripheral support, or a long frame needs review.
- `Failed` means the core threw, ran below real time, stopped advancing,
  produced invalid/dropped audio, or failed deterministic state restoration.
- `Unsupported` means the cartridge or peripheral is outside PixelNES's
  declared hardware envelope.
- `Invalid` means the image could not be inspected as a valid cartridge.

A pass is not proof that every level, timing-sensitive scene, or controller
peripheral works. Automated routes turn broad uncertainty into a small review
queue; reported gameplay defects and reference-ROM failures remain the
highest-value compatibility evidence.

## July 25, 2026 collection baseline

The PixelNES 1.15.022 600-frame run covered 818 local files representing 814
unique images and 30 mapper families:

| Result | Count |
| --- | ---: |
| Pass | 730 |
| Warning | 88 |
| Failed | 0 |
| Unsupported | 0 |
| Invalid | 0 |

All 818 routes restored the exact next frame and audio after save/load. No
route dropped an audio sample. The slowest measured core sustained 304.9 FPS,
well above the 60.0988 Hz NTSC requirement; the largest p99 frame was
8.829 ms. Of the 88 review flags, 82 were silent during the ten-second
window, 18 did not show multicolor video at a checkpoint, and 13 stayed
visually static. These observations are triage leads, not automatically
confirmed emulator defects.

Four duplicate-image pairs were also identified despite different filenames:
Bomberman 2, Captain America and the Avengers, Captain Comic, and Super Mario
Bros. 3.
