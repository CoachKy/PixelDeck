# Pixel64 compatibility laboratory

The Pixel64 compatibility laboratory runs a bounded, repeatable 600-field
audit against every `.z64`, `.v64`, and `.n64` cartridge image in
`Games/Nintendo64`.
It turns compatibility work into evidence rather than relying on a single
manual play session.

The audit is read-only:

- ROM bytes are only read and hashed.
- `N64Machine` is created without a battery-save path.
- No file is written into `Games` or `Saves`.
- All generated evidence stays under `artifacts/n64-compatibility`.

## Run it

```powershell
.\scripts\Test-Pixel64Compatibility.ps1
```

Useful focused runs:

```powershell
.\scripts\Test-Pixel64Compatibility.ps1 -Filter 'Mario 64' -FieldsPerGame 600
.\scripts\Test-Pixel64Compatibility.ps1 -NoCaptures
.\scripts\Test-Pixel64Compatibility.ps1 -Strict
```

`-Strict` returns a non-zero exit code when a cartridge is invalid or the core
fails. Warnings remain review items so an unverified cartridge or a slow,
static boot scene does not make automation unusable.

## Evidence

Each run creates:

- `REPORT.md` for the compatibility and blocker overview;
- `games.csv` for sorting and comparing counters;
- `report.json` for automation and future baseline comparisons;
- `captures/*.bmp` for the last available frame from warning/failure cases.

The report records cartridge identity, CIC, video region, save type, source
byte order, CPU progress, exact program counter, graphics and audio task
counts, unsupported HLE opcodes, unsupported texture formats, VI/AI activity,
controller polling, visual liveness, audio output, dropped samples,
performance, exact next-field save-state determinism, and the final RDP
other-mode/cycle state with alpha-rejection and framebuffer-blend counters.

## Graphics backend roadmap

`N64Machine` now submits graphics tasks through `IN64GraphicsBackend`. The
bundled implementation remains Pixel64's optimized Fast3D software renderer,
but the scheduler is no longer coupled directly to that implementation. This
is the seam for a future conformant RDP backend.

The current renderer decodes the RDP colour combiner for textured and
untextured spans, tracks blend/fog colours, applies alpha compare, and reads
the framebuffer for programmed blender cycles. These changes cover the
full-screen shade family used by Super Mario 64's pause UI. Coverage, dithering,
and two-cycle behavior remain approximations.

The next backend milestone is a Vulkan capability probe and a separate
paraLLEl-RDP adapter. It must remain optional, preserve the software fallback,
and pass identical scheduler, save-state, controller, and compatibility-lab
routes before it can become the preferred renderer.

## Reading the status

- **Pass**: this bounded route completed without a detected issue.
- **Warning**: the route completed, but the cartridge is unverified, below
  realtime, visually/audio inactive, or used unsupported HLE work.
- **Failed**: execution threw, the CPU did not advance, an unsupported CPU
  instruction was reached, audio integrity failed, or state replay diverged.
- **Invalid**: the file could not be read or was not a valid N64 cartridge.

A pass is not a claim that the entire game is compatible. Longer scripted
routes and per-title assertions can be layered onto this laboratory as
Pixel64's hardware coverage grows.
