# Pixel64 0.11 certification

Pixel64 0.11.016 is a compatibility milestone for the in-repository Nintendo
64 core. It expands audio-HLE coverage, adds CIC-specific startup behavior,
and gives Factor 5's Rogue Squadron graphics microcode a dedicated command
path. It is still a pre-release emulator milestone, not an RMG-equivalence or
general game-compatibility claim.

- Product versions must report Pixel64 `0.11.016` and PixelDeck `1.24.075`.
- Super Mario 64 (USA) revision 0 remains the verified gameplay route.
- Every structurally valid local cartridge remains launchable as an
  experimental attempt.
- The dashboard visuals and shared library behavior are unchanged.

## Audio coverage

`N64AudioProcessor` now identifies and executes four production audio-HLE
families:

- ABI-1, including the GoldenEye/Blast Corps envelope variant;
- NAudio, including the Banjo-Kazooie and Donkey Kong variants;
- NEAD, including the Mario Kart and Ocarina variants; and
- MusyX v1 structured tasks.

The ABI path now uses the fixed 64-phase, four-tap resampler coefficient ROM,
the hardware ADPCM history layout, the two-bit ADPCM path used by NEAD, exact
envelope continuation state, and aligned DMA behavior. A 195-field local Super
Mario 64 trace produced 189,856 bounded 32,006 Hz sample values with no clipped
samples, large discontinuities, unsupported commands, or output drops.

The Mario Kart NEAD route executed all 162,046 observed commands and produced
517,920 sample values without a drop. The Ocarina route executed all 106,661
observed commands and produced 610,560 sample values without a drop. Synthetic
MusyX coverage proves that an active PCM voice is decoded, resampled, enveloped,
mixed, and written as stereo output. Rogue Squadron's bounded startup route
does not yet activate a voice, so audible MusyX behavior in that title remains
unverified.

## CIC and cartridge startup

The machine now performs the CIC-6105 IPL2 RSP DMA handshake used by Donkey
Kong 64. The installed Donkey Kong 64 image reaches graphics and audio tasks
without an unsupported CPU instruction.

Cartridge entry-point publication now applies the IPL3 relocation associated
with CIC-6103 and CIC-6106. The installed Major League Baseball Featuring Ken
Griffey Jr. image changed from never reaching its cartridge entry point to
reaching cartridge code, polling controllers, submitting graphics work, and
producing visible-color activity during the bounded audit.

## Factor 5 graphics microcode

The renderer detects the Rogue Squadron Factor 5 microcode by its strict
word-swapped CRC32, `DA51CCDB`, and routes it through a dedicated display-list
parser. That parser understands Factor 5's software branch headers, control
flow, inline payload sizes, vertex loads, triangle payloads, texture rectangles,
viewport state, and extended other-mode mask.

The final bounded Rogue Squadron audit processed 795,688 graphics commands.
Only 22 unsupported commands remained, all belonging to the custom Factor 5
triangle-generation operation. Earlier generic parsing treated inline payload
data as commands and produced hundreds of thousands of false failures.

Factor 5 `TriGen` and `TexRectGen` semantics remain open. Their command streams
are now isolated and reported honestly rather than corrupting subsequent
display-list decoding.

## Installed-cartridge audit

A read-only 600-field audit of all 13 locally installed Nintendo 64 images
completed without a failed or invalid cartridge:

| Result | Count |
| --- | ---: |
| Pass | 1 |
| Warning | 12 |
| Failed | 0 |
| Invalid | 0 |

Super Mario 64 was the only verified pass. Warnings mean that a route is still
unverified, visually or audibly inactive during the bounded window, below the
required performance threshold, or used unsupported hardware work. They do not
mean the whole game is playable.

The run exercised ABI-1, NAudio, NEAD, and MusyX v1 audio; Fast3D, F3DEX2, and
Factor 5 graphics; CIC-6102, CIC-6103, and CIC-6105 startup paths; controller
polling; graphics-task capture; and exact next-field save-state replay.
CIC-6106 entry relocation has focused synthetic coverage but no installed
cartridge in this audit.

## Remaining release blockers

Pixel64 has not reached 1.0. The next high-value gaps are:

- exact Factor 5 `TriGen` and `TexRectGen` operations;
- MusyX v2 audio;
- audible-route verification for Rogue Squadron, GoldenEye, and Quest 64;
- extended gameplay verification beyond Super Mario 64;
- exact RDP coverage, blending, coverage, dithering, and VI behavior;
- additional graphics microcodes and enhancement hardware;
- low-level RSP execution for tasks that cannot be represented safely in HLE;
- longer deterministic title routes and on-device Raspberry Pi evidence.

## Reproducing the gates

```powershell
dotnet test tests/PixelDeck.App.Tests/PixelDeck.App.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~N64"

dotnet test tests/PixelDeck.App.Tests/PixelDeck.App.Tests.csproj `
  -c Release `
  --filter "FullyQualifiedName~ProductVersionTests"

dotnet build PixelDeck.sln -c Release

.\scripts\Test-Pixel64Compatibility.ps1 `
  -FieldsPerGame 600 `
  -Parallelism 3 `
  -GraphicsCaptures
```

The compatibility laboratory is diagnostic and read-only. Its generated
reports remain under `artifacts/n64-compatibility` and are not release
fixtures.
