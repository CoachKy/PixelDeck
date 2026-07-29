# PixelDeck v1.22.071

PixelDeck 1.22.071 adds automatic updates, gives every game a library image you
control, and ships a Raspberry Pi build alongside the Windows one for the first
time.

## Highlights

- **Automatic updates.** PixelDeck checks GitHub Releases while the loading
  splash is up and offers any newer version before the dashboard appears.
  Downloads are SHA-256 verified, installed by a separate updater process, and
  rolled back automatically if anything fails, so a bad update cannot leave you
  without a working application. The check is skippable, times out in eight
  seconds, and never blocks startup â€” no network, no delay.
- **Library images you choose.** A game that already has a library image is no
  longer overwritten by an automatic screenshot. **Take New Library Image** in
  the pause menu captures the current frame on demand, with the pause overlay
  excluded from the shot. Images live in a visible `Library/<Platform>/` folder
  so you can drop in your own artwork.
- **Raspberry Pi package.** A `linux-arm64` build is published from the same
  release as Windows. The updater picks the right package for the machine it is
  running on, so both platforms update from a single tag.
- **A folder you can actually read.** Previous builds extracted to 239 loose
  files, 230 of them DLLs. Everything is now linked into the executable itself,
  leaving the program, four graphics libraries, the updater, and your
  `Games`, `Saves`, and `Library` folders â€” which ship with the package instead
  of appearing only after the first launch. Roughly 100 MB of unused native
  debug symbols have also been dropped from the download.
- **PixelSNES enhancement-chip progress.** SA-1 and S-DD1 titles that previously
  failed to boot or rendered as noise now run. Super FX titles are labelled
  **Partial** in the library rather than presented as supported.

## Cores in this build

| Core | Version | Status |
| --- | --- | --- |
| PixelNES | 1.15.023 | Release |
| PixelSNES | 1.16.023 | Release core; Super FX remains incomplete |
| Pixel64 | 0.9.014 | Pre-release, experimental |

### PixelSNES 1.16.023

- **SA-1 now boots its library.** The coprocessor's internal RAM is mirrored into
  the low 2 KiB where the 65C816 keeps its direct page and stack; without that,
  every subroutine return landed on garbage. Super Mario RPG, Kirby Super Star,
  and Kirby's Dream Land 3 run as a result.
- **S-DD1 decompression corrected.** Paired bitplane modes decode a plane pair
  one bit at a time rather than a byte at a time, which matters because the
  probability model advances per bit. Star Ocean renders.
- **Audio allocation removed.** The DSP was building three delegates per voice
  per sample â€” roughly 800 KB of garbage a frame â€” which showed up as periodic
  collection stutter. This affects every SNES title, not just enhanced ones.
- **Known exclusion: Super FX (GSU-1/GSU-2) does not render.** The GSU core runs
  and the cartridges load, but no verified 3D output is produced. Star Fox,
  Star Fox 2, Stunt Race FX, and Yoshi's Island are flagged **Partial** in the
  library. Titles in that group with 2D title screens and menus will show them;
  that is not Super FX output.
- Street Fighter Alpha 2 (S-DD1) stalls early and is not playable.

### Pixel64 0.9.014 â€” bonus, experimental

Nintendo 64 support remains a bonus and is **not** a general-compatibility
claim. Super Mario 64 is the verified gameplay route; most cartridges outside
the emulated microcode set cannot draw.

## Installing

### Windows

1. Download `PixelDeck-v1.22.071-win-x64.zip`.
2. Extract the entire folder.
3. Run `PixelDeck.App.exe`.

### Raspberry Pi (64-bit)

1. Download `PixelDeck-v1.22.071-linux-arm64.tar.gz`.
2. `tar -xzf PixelDeck-v1.22.071-linux-arm64.tar.gz`
3. `./PixelDeck.App`

The tarball is used instead of a zip because zip does not carry the Unix execute
bit, which would leave the extracted binary unrunnable.

Both packages are self-contained and include the required .NET runtime. They are
portable: no installer, no registry entries, no administrator rights. Windows
SmartScreen may warn that the publisher is unknown because the build is not
code-signed; choose **More info â†’ Run anyway**.

Place legally obtained cartridges in the included `Games/Nintendo`,
`Games/SuperNintendo`, and `Games/Nintendo64` folders. **No ROM images are
distributed with PixelDeck.**

## Notes on this release

- **This update must be installed by hand.** Automatic updating begins *from*
  this version â€” 1.19.062 and earlier have no updater to notify you. Once
  1.22.071 is running, later releases are offered on startup.
- **The Raspberry Pi package has not been validated on Pi hardware.** It is
  built, correctly laid out, and verified to contain a Linux ARM64 executable at
  its root, but it has not been run on a device. Treat it as a first attempt.
- Both packages belong to a single release tagged `v1.22.071`. The updater reads
  the latest release and selects the asset matching the machine it is on, so the
  two platforms must not be split across separate tags.

## Verifying the download

`PixelDeck-v1.22.071-win-x64.zip`:

```text
PENDING-REBUILD
```

`PixelDeck-v1.22.071-linux-arm64.tar.gz`:

```text
PENDING-REBUILD
```

```powershell
Get-FileHash PixelDeck-v1.22.071-win-x64.zip -Algorithm SHA256
```

```bash
sha256sum -c PixelDeck-v1.22.071-linux-arm64.tar.gz.sha256
```
