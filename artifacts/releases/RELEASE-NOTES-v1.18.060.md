# PixelDeck v1.18.060

PixelDeck's first 1.x release. A single console-style dashboard for playing
Nintendo, Super Nintendo, and Nintendo 64 cartridges from one library, with two
of its three emulation cores now certified as releases.

## Highlights

- **PixelSNES reaches 1.0.** The Super Nintendo core is promoted from a
  development build to its first release after sustained gameplay validation
  across the local library. It plays effectively the whole standard cartridge
  envelope.
- **PixelNES 1.15** continues as the certified Nintendo core, covering 30 mapper
  families with a full compatibility-lab audit behind it.
- **Nintendo 64 support gains audio.** Pixel64 is a bonus core and remains
  pre-release, but Super Mario 64 now produces sound for the first time.

## Cores in this build

| Core | Version | Status |
| --- | --- | --- |
| PixelNES | 1.15.022 | Release |
| PixelSNES | 1.15.020 | Release |
| Pixel64 | 0.5.006 | Pre-release, experimental |

### PixelNES 1.15.022

Certified Nintendo core. NTSC timing at an exact 60.0988 Hz frame clock, MMC5
expansion audio, selectable PPU revisions, optional OAM decay, and an
eight-sprite-limit toggle. Evidence is recorded in the
[PixelNES 1.15 certification](docs/PIXELNES-1.15-CERTIFICATION.md).

### PixelSNES 1.15.020 — new release

NTSC LoROM, HiROM, and ExHiROM images, including FastROM header variants and
standard ROM, RAM, battery-backed RAM, DSP-1, and Capcom CX4 cartridge types.
Copier-headered and headerless images are both accepted. Gameplay, audio,
battery saves, and save states are validated against the local library.

**Known exclusion: the Super FX (GSU-1/GSU-2) coprocessor is not implemented.**
Star Fox, Super Mario World 2: Yoshi's Island, Stunt Race FX, and Doom will not
run correctly. Super FX is the next planned PixelSNES feature. Full scope is in
the [PixelSNES 1.15 certification](docs/PIXELSNES-1.15-CERTIFICATION.md).

### Pixel64 0.5.006 — bonus, experimental

Nintendo 64 support is a bonus and is **not** a general-compatibility claim.
New in this build: audio microcode emulation (Super Mario 64 has sound), TLB
demand paging, per-cartridge video resolution, and vertex lighting.

- Super Mario 64 (USA rev 0) is the only verified gameplay route, and its
  graphics output is still partial — the title logo renders as a tiled grid.
- GoldenEye 007 boots, demand-pages, and renders its intro geometry.
- Only the Fast3D microcode is emulated, so most other cartridges cannot draw.
- Only 512-byte EEPROM saves are supported.

Details and the full defect list are in the
[Pixel64 0.5 certification](docs/PIXEL64-0.5-CERTIFICATION.md).

## Installing

1. Download `PixelDeck-v1.18.060-win-x64.zip`.
2. Extract the whole folder anywhere you like.
3. Run `PixelDeck.App.exe`.

The build is self-contained — the .NET runtime is included, so nothing needs to
be installed. It is portable: no installer, no registry entries, no admin
rights. Windows SmartScreen may warn that the publisher is unknown because the
build is not code-signed; choose **More info → Run anyway**.

Place cartridges in the `Games/Nintendo`, `Games/SuperNintendo`, and
`Games/Nintendo64` folders included in the package. **No ROMs are distributed
with PixelDeck.**

## Verifying the download

SHA-256 of `PixelDeck-v1.18.060-win-x64.zip`:

```
d9f6f35666394492b7a79b55eb116617bc91548899a1150b5732a275d43f3420
```

```powershell
Get-FileHash PixelDeck-v1.18.060-win-x64.zip -Algorithm SHA256
```
