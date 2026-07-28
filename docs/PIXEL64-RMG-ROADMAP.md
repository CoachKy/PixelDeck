# Pixel64 RMG-aligned roadmap

Pixel64 remains PixelDeck's in-repository Nintendo 64 emulator. The goal is
not to turn it into a thin RMG or Mupen64Plus launcher. The goal is to adopt
the mature separation of responsibilities used by RMG while preserving
Pixel64's own machine, debugger, save-state, input, and compatibility routes.

RMG is organized around a frontend/core plus interchangeable graphics, RSP,
audio, and input plugins. Pixel64 now has the first equivalent seam:
`N64Machine` schedules graphics work through `IN64GraphicsBackend`, with the
existing Fast3D software renderer as the bundled fallback.

## Current component map

| Responsibility | Pixel64 component | Status |
| --- | --- | --- |
| Frontend and lifecycle | PixelDeck Avalonia application | In place |
| CPU, memory, devices, scheduler | `N64Machine`, `Vr4300Cpu`, `N64Memory` | In place; incomplete hardware coverage |
| Graphics task boundary | `IN64GraphicsBackend` | In place |
| Bundled HLE renderer | `Fast3dRenderer` | In place; approximate RDP behavior |
| Audio task processor | `N64AudioProcessor` | Separate component; interface boundary still needed |
| Controller input | PixelDeck controller snapshot and PIF handling | In place |
| Per-title evidence | Pixel64 compatibility laboratory | In place |
| RDP command dump and replay | — | Next milestone |
| Conformant RDP backend | — | Planned |
| Interchangeable RSP backend | — | Planned |
| Per-game compatibility profiles | — | Planned |

## Ordered milestones

### 1. Stabilize the software renderer

- Keep the current renderer as a deterministic, portable fallback.
- Complete combiner, alpha comparison, framebuffer blending, coverage,
  dithering, two-cycle behavior, texture filtering, and VI presentation.
- Record final RDP state and high-value counters in every compatibility run.
- Lock Mario pause-screen shade and similar regressions with focused tests.

### 2. Add RDP dump and replay

- Capture the RDP command stream and referenced RDRAM/TMEM state independently
  of the running CPU.
- Build a deterministic replay tool that can render one captured frame through
  any `IN64GraphicsBackend`.
- Store image hashes and selected pixel assertions for regression comparison.
- Keep ROM data and copyrighted assets out of committed fixtures.

This is the highest-value next step: it makes graphics bugs reproducible in
seconds and gives every future backend the same conformance input.

### 3. Add an optional conformant RDP backend

- Add a runtime Vulkan capability probe.
- Integrate an MIT-compatible paraLLEl-RDP adapter behind
  `IN64GraphicsBackend`.
- Keep the Fast3D software renderer available when Vulkan is absent.
- Require both backends to use the same scheduler, controller, save-state, and
  compatibility-lab routes.
- Compare replay output before changing the preferred backend.

### 4. Separate the RSP

- Introduce an `IN64RspBackend` boundary for graphics and audio task execution.
- Preserve the current HLE processors as the portable fallback.
- Add an LLE-capable backend only after task lifecycle, interrupts, and
  save-state ownership are explicit.

### 5. Introduce per-game profiles

- Key settings by cartridge identity rather than filename.
- Limit profiles to verified compatibility choices such as backend, RSP mode,
  framebuffer emulation, and timing workarounds.
- Keep defaults correct and profiles auditable; avoid title-specific rendering
  hacks hidden inside generic code.

### 6. Grow conformance and release evidence

- Run dump/replay fixtures in CI.
- Add longer scripted title routes for owned local cartridges.
- Track backend, unsupported commands, performance, audio integrity, and exact
  state replay in compatibility reports.
- Require manual play verification before upgrading a title from `PARTIAL`.

## Licensing boundary

RMG is GPL-3.0 and is an architectural reference, not a source of code for
PixelDeck's MIT-licensed core. Direct code reuse would require a deliberate
licensing decision. paraLLEl-RDP is MIT-licensed and is the preferred candidate
for a future native graphics adapter, subject to integration and attribution
review.
