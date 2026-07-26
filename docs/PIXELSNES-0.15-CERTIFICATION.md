# PixelSNES 0.15 certification

PixelSNES 0.15.019 makes *The Legend of Zelda: A Link to the Past* a
flagship compatibility route. This is a bounded, repeatable gameplay gate,
not a claim that every room, effect, or ending has been certified.

## Certified local route

When the user's local NTSC LoROM image is present, the automated route starts
with empty cartridge SRAM and verifies all of the following:

- the intro and file-select code complete without BRK, COP, reset re-entry, or
  an unsupported SPC700 opcode;
- a new player is created and committed to cartridge SRAM;
- the game enters the normal indoor gameplay module in Link's house;
- the scene is visible and contains at least 24 colors;
- Link's X position advances when Right is held;
- S-DSP output is audible and the bounded consumer reports no dropped samples;
- a gameplay save state reproduces the exact next video frame and audio
  samples after restoration;
- flushing SRAM creates a non-empty, durable 8 KiB battery save.

The current local evidence reached a 52-color in-house scene, moved Link from
X 2399 to X 2506, produced a 0.4976 peak, and completed without a CPU or APU
invalid path.

## PPU improvement

SNES register `$2106` controls mosaic size and layer selection. PixelSNES
previously grouped every vertical mosaic block from scanline zero. Version
0.15.019 records the live vertical counter whenever the register value changes
during the visible field, restarts the vertical grouping from that scanline,
and resets the phase at the next field. This improves the stepped transition
effects used by A Link to the Past and other games.

Save-state format 14 preserves the in-flight mosaic phase. Formats 10 through
13 remain loadable and migrate with a top-of-field phase.

## Reproduce

Run the focused flagship gate:

```powershell
dotnet test tests/PixelDeck.App.Tests/PixelDeck.App.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~SnesMachineTests.LocalLinkToThePastCreatesAPlayerAndReachesControllableGameplayWhenPresent"
```

Run the complete PixelSNES release suite:

```powershell
./scripts/Test-PixelSnesRelease.ps1
```

No game image or proprietary Nintendo code is stored in the repository. The
real-game gate skips when the user's local image is absent.

## Remaining Zelda work

The current gate does not yet certify the rain overworld, castle traversal,
dungeons, Mode 7 map scenes, the ending, or an uninterrupted full-game soak.
Those should become additional deterministic checkpoints before PixelSNES 1.0.
