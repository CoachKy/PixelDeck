# Pixel64 0.10 certification

Pixel64 0.10.015 adds the missing ABI-1 recursive pole filter and runs the
current local Nintendo 64 collection through the compatibility laboratory.
It remains a pre-release emulator milestone, not a general-compatibility
claim.

- Product versions must report Pixel64 `0.10.015` and PixelDeck `1.23.074`.
- Super Mario 64 (USA) revision 0 remains the only verified gameplay route.
- Every structurally valid cartridge remains launchable as an experimental
  attempt even when its graphics or audio microcode is not implemented.

## ABI-1 pole filter

`N64AudioProcessor` now implements ABI-1 opcode `0x0E`, `A_POLEF`:

- signed Q14 input gain;
- both eight-tap codebook responses;
- feedback from the previous output group;
- recursive feedback from inputs already processed in the current group;
- signed 16-bit saturation;
- initialization and four-sample continuation state in RDRAM; and
- deterministic state serialization through the existing audio-backend state.

A synthetic exact-vector regression checks all 16 output samples, the four
persisted continuation samples, and the unsupported-command counter.

## Audio microcode identity

The audio backend now reads the task's resident ucode-data signature before
dispatch. It distinguishes standard ABI-1, the GoldenEye and Blast Corps ABI-1
variants, NAudio variants, NEAD variants, and MusyX. GoldenEye/Blast Corps use
their variant's signed per-sample linear envelope ramps and 80-byte continuation
record; a focused regression checks its eight-sample ramp and saved state.

Known alternate-family lists are not sent through the ABI-1 decoder. Every
command is reported as unsupported until its own decoder exists. Unknown
signatures retain the ABI-1 fallback so homebrew and synthetic tests are not
rejected solely because their ucode-data block has no known production
signature.

## Collection audit

The same read-only 120-field audit was run before and after the implementation
against all 13 locally discovered cartridge images:

```powershell
.\scripts\Test-Pixel64Compatibility.ps1 `
  -FieldsPerGame 120 `
  -Parallelism 4 `
  -NoCaptures
```

Both runs completed with 13 warnings, zero failures, and zero invalid images.
The warnings are expected for unverified or incomplete routes. No cartridge
encountered an unsupported CPU instruction during this bounded boot window.

The following genuine ABI-1 `0x0E` counts fell to zero:

| Cartridge | Before | After |
| --- | ---: | ---: |
| GoldenEye 007 | 375 | 0 |
| Pilotwings 64 | 801 | 0 |
| Quest 64 | 273 | 0 |

This counter improvement does not certify audible output or gameplay in those
titles.

The same run identified the active audio family per cartridge:

| Family | Cartridges active in the 120-field window |
| --- | --- |
| ABI-1 | Pilotwings 64, Quest 64, Super Mario 64 |
| ABI-1 GoldenEye variant | GoldenEye 007 |
| NAudio | Mario Golf, Mario Tennis, WWF WrestleMania 2000 |
| NEAD Mario Kart variant | Mario Kart 64 |
| MusyX v1 | Star Wars: Rogue Squadron |

Other cartridges did not submit an identifiable audio task in the bounded
window. NAudio, NEAD, and MusyX command execution remain outside this
milestone. The next audio target is a separately tested NAudio decoder, then
the Mario Kart NEAD variant. Treating either as extensions to the ABI-1 table
would corrupt command parameters and hide the real compatibility gap.

## Reproducing the gates

```powershell
dotnet test tests/PixelDeck.App.Tests/PixelDeck.App.Tests.csproj `
  --filter "FullyQualifiedName~N64"

dotnet test tests/PixelDeck.App.Tests/PixelDeck.App.Tests.csproj `
  --filter "FullyQualifiedName~ProductVersionTests"

dotnet build PixelDeck.sln -c Release
```

The compatibility laboratory is diagnostic and read-only. Its generated
reports remain under `artifacts/n64-compatibility` and are not release
fixtures.
