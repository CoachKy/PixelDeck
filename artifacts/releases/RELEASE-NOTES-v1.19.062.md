# PixelDeck v1.19.062

PixelDeck 1.19.062 expands controller configuration and advances the in-repository
Super Nintendo and Nintendo 64 cores.

## Highlights

- **Expanded controller setup.** Nintendo 64 profiles now expose all four
  controller ports with independent device assignments and button mappings.
  Controller status identifies the active backend and detected devices.
- **PixelSNES enhancement hardware.** PixelSNES introduces in-repository SA-1,
  S-DD1, and Super FX implementations, along with cartridge integration,
  diagnostics, trace tooling, and performance coverage.
- **Pixel64 improvements.** Nintendo 64 work advances cartridge boot behavior,
  memory and serial-interface handling, rendering, save-state coverage, and
  multi-controller support.

## Cores in this build

| Core | Version | Status |
| --- | --- | --- |
| PixelNES | 1.15.023 | Release |
| PixelSNES | 1.15.022 | Release core; new enhancement-chip coverage remains experimental |
| Pixel64 | 0.9.008 | Pre-release, experimental |

Pixel64 is not yet a general Nintendo 64 compatibility claim. The new SA-1,
S-DD1, and Super FX paths also require additional title-by-title certification
before they should be treated as complete implementations.

## Installing

1. Download `PixelDeck-v1.19.062-win-x64.zip`.
2. Extract the entire folder.
3. Run `PixelDeck.App.exe`.

The package is self-contained and includes the required .NET runtime. It is
portable and does not require an installer, registry changes, or administrator
rights. Windows SmartScreen may warn that the publisher is unknown because the
build is not code-signed.

Place legally obtained cartridges in the included `Games/Nintendo`,
`Games/SuperNintendo`, and `Games/Nintendo64` folders. No ROM images are
distributed with PixelDeck.

## Verifying the download

SHA-256 of `PixelDeck-v1.19.062-win-x64.zip`:

```text
36185cb659b2e6d16e94b640d5ff689f0b86a476985503a5bd1bc0c6f8f9fcee
```

```powershell
Get-FileHash PixelDeck-v1.19.062-win-x64.zip -Algorithm SHA256
```
