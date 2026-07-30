# PixelDeck v1.22.073

PixelDeck 1.22.073 rebuilds how the application is packaged. The download is
**44 MB instead of 74 MB**, the install folder holds five files instead of 239,
and future updates are about **600 KB** rather than a full re-download.

It also repairs the update system, which did not work in 1.22.070 or 1.22.071.
**This release has to be installed by hand** — see
**Updating from an earlier version** below.

## Highlights

- **A stable launcher.** `PixelDeck.exe` now contains only startup, crash
  handling, logging, and update installation. The dashboard and the emulation
  cores live in a `Components` folder beside it. Releases that change those — the
  overwhelming majority — leave the executable byte-for-byte identical.
- **Updates are small.** Because a release usually changes only the components,
  the updater downloads roughly 600 KB instead of the whole package. It falls
  back to the full download when the launcher itself changed, and verifies a
  SHA-256 for every file before overwriting anything.
- **One fewer program.** `PixelDeck.Updater.exe` is gone. It existed only because
  Windows will not let a running process overwrite its own files; the launcher
  now installs updates during startup, before it loads anything replaceable, so
  nothing is locked and no helper is needed. That alone removed 70 MB from the
  download.
- **Automatic updates that work.** The updater in 1.22.070 and 1.22.071 tried to
  overwrite its own running executable, which Windows refuses. The failure was
  indistinguishable from any other, so every update rolled itself back.
- **Your ROM library is no longer copied on every update.** The safety backup
  covered the whole install folder. Only the files an update actually replaces
  are copied aside now, and user content is not on the list of files an update is
  permitted to touch at all.
- **The loading splash no longer reappears mid-session.** Quitting a game started
  a library rescan, and the splash was tied to the rescan rather than to startup.

## What the folder looks like

```text
PixelDeck.exe            the launcher
libSkiaSharp.dll         graphics and audio libraries
av_libglesv2.dll
SDL3.dll
libHarfBuzzSharp.dll
Components/              application and emulation cores; updates replace these
Games/                   your cartridges, per system
Saves/                   battery saves and save states
Library/                 cover images
```

Nothing under `Games`, `Saves`, or `Library` is ever written by an update.

## Cores in this build

| Core | Version | Status |
| --- | --- | --- |
| PixelNES | 1.15.023 | Release |
| PixelSNES | 1.16.023 | Release core; Super FX remains incomplete |
| Pixel64 | 0.9.014 | Pre-release, experimental |

Launcher version 1.0.0, tracked separately from the release number so an update
can tell whether it needs to replace the executable.

### PixelSNES 1.16.023

- **SA-1 now boots its library.** The coprocessor's internal RAM is mirrored into
  the low 2 KiB where the 65C816 keeps its direct page and stack; without that,
  every subroutine return landed on garbage. Super Mario RPG, Kirby Super Star,
  and Kirby's Dream Land 3 run as a result.
- **S-DD1 decompression corrected.** Paired bitplane modes decode a plane pair
  one bit at a time rather than a byte at a time, which matters because the
  probability model advances per bit. Star Ocean renders.
- **Audio allocation removed.** The DSP was building three delegates per voice
  per sample — roughly 800 KB of garbage a frame — which showed up as periodic
  collection stutter. This affects every SNES title.
- **Known exclusion: Super FX (GSU-1/GSU-2) does not render.** The GSU core runs
  and the cartridges load, but no verified 3D output is produced. Star Fox,
  Star Fox 2, Stunt Race FX, and Yoshi's Island are flagged **Partial** in the
  library. Titles in that group with 2D title screens will show them; that is not
  Super FX output. Yoshi's Island also produces no audio.
- Street Fighter Alpha 2 (S-DD1) stalls early and is not playable.

### Pixel64 0.9.014 — bonus, experimental

Nintendo 64 support remains a bonus and is **not** a general-compatibility
claim. Super Mario 64 is the verified gameplay route; most cartridges outside the
emulated microcode set cannot draw.

## Installing

### Windows

1. Download `PixelDeck-v1.22.073-win-x64.zip`.
2. Right-click it, choose **Properties**, tick **Unblock**, and apply. Windows
   otherwise marks everything inside as downloaded and warns when you run it.
3. Extract the whole folder.
4. Run `PixelDeck.exe`.

### Raspberry Pi (64-bit)

1. Download `PixelDeck-v1.22.073-linux-arm64.tar.gz`.
2. `tar -xzf PixelDeck-v1.22.073-linux-arm64.tar.gz`
3. `./PixelDeck`

The tarball is used instead of a zip because zip does not carry the Unix execute
bit, which would leave the extracted binary unrunnable.

Both packages are self-contained and include the required .NET runtime. They are
portable: no installer, no registry entries, no administrator rights. Windows
SmartScreen may warn that the publisher is unknown because the build is not
code-signed; choose **More info → Run anyway**.

Cartridges go in `Games/Nintendo`, `Games/SuperNintendo`, and
`Games/Nintendo64`. **No ROM images are distributed with PixelDeck.**

## Updating from an earlier version

**Install this release by hand, whichever version you are on.**

- **1.22.070 and 1.22.071** shipped the broken updater described above, so they
  cannot update themselves to anything.
- **1.19.062 and earlier** predate the updater entirely.
- The program is also named differently now — `PixelDeck.exe` rather than
  `PixelDeck.App.exe` — so any shortcut you made needs repointing.

Extract this release over your existing folder. The launcher removes the old
`PixelDeck.App.exe`, the retired updater, and the loose assemblies from the
previous layout on its first run, so nothing stale is left to launch by mistake.
Your `Games`, `Saves`, and `Library` folders are untouched.

From 1.22.073 onward, updates are offered on startup and install themselves.

## Notes on this release

- **The Raspberry Pi package has not been validated on Pi hardware.** It is
  built, correctly laid out, and verified to contain a Linux ARM64 executable at
  its root, but it has not been run on a device.
- All packages and `manifest.json` belong to a single release tagged
  `v1.22.073`. The updater reads the asset list to pick between the component
  archive and the package for its own platform, so they must not be split across
  tags.
- A launcher trace log is written to
  `%LOCALAPPDATA%\PixelDeck\launcher.log`. It records versions, update decisions,
  and load failures — no ROM paths or personal information.

## Verifying the download

`PixelDeck-v1.22.073-win-x64.zip`:

```text
747b2f00107bfc8526ac6148034d379d4dd8d9dd6bf68a6b26ae40ba9b7e7fb9
```

`PixelDeck-v1.22.073-linux-arm64.tar.gz`:

```text
ff2f9846d4304f61234bb6dd8a446a8e447e0a843eaa6678672ae5a1494025d9
```

`PixelDeck-v1.22.073-components-launcher1.0.0.zip`:

```text
f092b88c07b0cd99496c91b3a49808412d73abe0b367404519743e436dea58d1
```

```powershell
Get-FileHash PixelDeck-v1.22.073-win-x64.zip -Algorithm SHA256
```

```bash
sha256sum -c PixelDeck-v1.22.073-linux-arm64.tar.gz.sha256
```
