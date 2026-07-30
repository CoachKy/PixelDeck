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
.\scripts\Test-Pixel64Compatibility.ps1 -Filter 'Mario 64' -GraphicsCaptures
.\scripts\Test-Pixel64Compatibility.ps1 -Strict
```

`-Strict` returns a non-zero exit code when a cartridge is invalid or the core
fails. Warnings remain review items so an unverified cartridge or a slow,
static boot scene does not make automation unusable.

`-GraphicsCaptures` asks each machine to capture its first submitted graphics
task. A short audit that does not reach a display list reports that honestly as
a warning instead of creating synthetic evidence.

## Evidence

Each run creates:

- `REPORT.md` for the compatibility and blocker overview;
- `games.csv` for sorting and comparing counters;
- `report.json` for automation and future baseline comparisons;
- `captures/*.bmp` for the last available frame from warning/failure cases.
- `graphics-tasks/*.p64gfx` for the first pre-execution graphics task when
  `-GraphicsCaptures` is enabled.

The `.p64gfx` format contains the RSP task descriptor and an exact compressed
snapshot of the 8 MiB RDRAM visible to that task. It does not contain the full
cartridge image, but RDRAM can include game-derived code and assets; captures
are local diagnostic evidence and must not be committed or distributed without
permission. Each file has a versioned header and SHA-256 integrity check, and
malformed, truncated, oversized, or trailing data is rejected.

Replay a capture without booting the game:

```powershell
dotnet run --project tools/PixelDeck.N64GraphicsReplay -- `
  'artifacts/n64-compatibility/<run>/graphics-tasks/<capture>.p64gfx' `
  --repeat 3
```

Export the direct native RDP packet stream, then replay it without running the
Fast3D display-list decoder:

```powershell
dotnet run --project tools/PixelDeck.N64GraphicsReplay -- `
  'artifacts/n64-compatibility/<run>/graphics-tasks/<capture>.p64gfx' `
  --export-rdp 'artifacts/n64-compatibility/<run>/graphics-tasks/<capture>.p64rdp'

dotnet run --project tools/PixelDeck.N64GraphicsReplay -- `
  'artifacts/n64-compatibility/<run>/graphics-tasks/<capture>.p64rdp' `
  --repeat 3

# Complete native-packet traces can also use the optional Vulkan backend.
dotnet run --project tools/PixelDeck.N64GraphicsReplay -- `
  'artifacts/n64-compatibility/<run>/graphics-tasks/<capture>.p64rdp' `
  --repeat 3 --parallel-rdp
```

The `.p64rdp` format has a versioned header, compressed 8 MiB pre-task RDRAM,
ordered variable-length native packets, microcode metadata, and a SHA-256
checksum over the logical contents. It rejects truncation, oversized records,
checksum mismatches, and trailing data. Ordinary clipped Fast3D/F3DEX/F3DEX2
triangles are lowered into 44-word native shade/texture/depth packets. The
format still records exact omissions for HLE lines or incomplete custom
generators. Only a trace with zero omissions and zero unsupported source
commands is reported as complete.

The replay utility reports input and output memory hashes, backend identity,
command coverage, render-target state, and whether repeated executions were
deterministic. ABI-2 native replay additionally identifies framebuffer and
depth regions and prints their exact changed-byte counts and hashes alongside
the 4 MiB hidden-coverage result.

The report records cartridge identity, CIC, video region, save type, source
byte order, CPU progress, exact program counter, graphics and audio task
counts, detected graphics and audio microcode families, strict graphics
microcode CRC, unsupported HLE opcodes, unsupported texture formats, VI/AI
activity, controller polling, visual liveness, audio output, dropped samples,
performance, exact next-field save-state determinism, and the final RDP
other-mode/cycle state with alpha-rejection and framebuffer-blend counters.

The current HLE family set includes Fast3D, F3DEX2, and the Factor 5 Rogue
Squadron command stream on graphics tasks, plus ABI-1, NAudio, NEAD, and MusyX
v1 on audio tasks. A detected family is evidence that its dispatcher ran, not
proof that every title or every command variant is correct.

## Graphics backend and replay roadmap

`N64Machine` now submits graphics tasks through `IN64GraphicsBackend`. The
bundled implementation remains Pixel64's optimized Fast3D software renderer,
but the scheduler is no longer coupled directly to that implementation. This
is the seam for a future conformant RDP backend.

The current renderer decodes the RDP colour combiner for textured and
untextured spans, tracks blend/fog colours, applies alpha compare, and reads
the framebuffer for programmed blender cycles. These changes cover the
full-screen shade family used by Super Mario 64's pause UI. Coverage, dithering,
and two-cycle behavior remain approximations.

Graphics-task capture/replay is the first conformance layer around that
boundary. Direct native RDP packet capture/replay is now the second. The Vulkan
capability probe and pinned paraLLEl-RDP native adapter are available for
complete-trace evaluation on identical renderer inputs. The adapter transfers
canonical RDRAM and hidden coverage, queues native packets, applies VI state,
and can return RGBA scanout. Ordinary native triangle-edge lowering, native
output-region hashing, a ROM-free color/depth/coverage CI gate, and a fail-safe
live opt-in are now present. Set
`PIXELDECK_N64_VIDEO_BACKEND=parallel-rdp` only with a tested native runtime:
Pixel64 executes software first and retains it after any incomplete or failed
native task. Selected real-game image assertions and multi-task hidden-state
continuity remain the gate before the native path becomes a dashboard setting
or preferred renderer. `scripts/Test-Pixel64ParallelRdp.ps1` now performs that
owned-local sequence check without persisting cartridge-derived captures.

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
