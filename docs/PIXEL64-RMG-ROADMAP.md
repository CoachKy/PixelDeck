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
| Audio task boundary | `IN64AudioBackend` | In place |
| Bundled audio HLE | `N64AudioProcessor` | ABI-1, NAudio, NEAD, and MusyX v1 in place; MusyX v2 remains |
| Controller input | PixelDeck controller snapshot and PIF handling | In place |
| Per-title evidence | Pixel64 compatibility laboratory | In place |
| Graphics-task capture and replay | `.p64gfx`, `N64GraphicsReplay` | In place |
| Raw RDP command dump and replay | `.p64rdp`, `N64RdpReplay` | Direct packets and ordinary HLE triangles in place; lines/custom generators remain |
| Conformant RDP backend | `PixelDeck.ParallelRdp`, `ParallelRdpNative` | Pinned native ABI v2, hidden-coverage readback, output deltas, multi-task replay, and fail-safe live opt-in in place; the owned-local real-game sequence must still be executed |
| Interchangeable RSP backend | `IN64RspBackend`, `N64RspProcessor` | In place; instruction-level scalar/vector execution, SP DMA, register state, and HLE fallback seam active |
| CIC boot behavior | `N64Machine`, `N64Memory`, `N64Cartridge` | CIC-6103/6106 entry relocation and CIC-6105 IPL2 handshake in place |
| Custom graphics microcode | `Fast3dRenderer` | Factor 5 Rogue Squadron path in place; custom generator operations remain |
| Per-game compatibility profiles | `N64GameProfile`, `N64GameProfileRegistry` | In place; cartridge-code profiling, RSP execution mode, save type, and CIC overrides active |

## Ordered milestones

### 1. Stabilize the software renderer

- Keep the current renderer as a deterministic, portable fallback.
- Complete combiner, alpha comparison, framebuffer blending, coverage,
  dithering, two-cycle behavior, texture filtering, and VI presentation.
- Record final RDP state and high-value counters in every compatibility run.
- Lock Mario pause-screen shade and similar regressions with focused tests.

### 2. Add RDP dump and replay

- Capture a versioned graphics task and its exact pre-execution RDRAM without
  storing the full cartridge image. **Complete.**
- Build a deterministic replay tool that can execute a capture through any
  `IN64GraphicsBackend`. **Complete.**
- Extract a backend-neutral raw RDP command stream plus referenced RDRAM/TMEM
  state independently of Fast3D display-list decoding. **Direct packet
  capture/replay and ordinary HLE triangle lowering complete; lines and custom
  generators remain.**
- Store image hashes and selected pixel assertions for regression comparison.
- Keep ROM data and copyrighted assets out of committed fixtures.

The `.p64rdp` stage now captures canonical RDP state, image, texture, combiner,
fill, sync, scissor, color, and texture-rectangle packets against exact
pre-task RDRAM. Legacy other-mode commands are normalized and segmented image
addresses are resolved before storage. Ordinary clipped Fast3D/F3DEX/F3DEX2
triangles now emit 44-word shade/texture/depth packets. A trace remains
incomplete for unlowered HLE lines, incomplete custom generators, or
unsupported source commands. Native replay now identifies framebuffer and
depth ranges and reports exact before/after hashes and changed-byte counts,
including paraLLEl-RDP's hidden-coverage memory. A ROM-free synthetic triangle
gate requires deterministic changes to all three targets in Linux CPU-Vulkan
CI; captured Mario image assertions remain required before Pixel64 can claim
the same validated low-level envelope as paraLLEl-RDP.

### 3. Add an optional conformant RDP backend

- Add a runtime Vulkan capability probe. **Complete.**
- Integrate an MIT-compatible paraLLEl-RDP adapter behind
  `IN64GraphicsBackend`. **Native C ABI v2, managed loader, RDRAM and hidden
  coverage transfer, command submission, VI state, scanout, complete-trace
  replay, triangle lowering, output-region hashing, and fail-safe live opt-in
  are in place. Multi-task replay preserves native hidden coverage while
  replacing each task's CPU-visible RDRAM with its exact capture.**
- Keep the Fast3D software renderer available when Vulkan is absent.
  **Complete and still the live default.**
- Require both backends to use the same scheduler, controller, save-state, and
  compatibility-lab routes.
- Compare replay output before changing the preferred backend. **The replay
  CLI accepts `--parallel-rdp` and reports framebuffer, depth, coverage, and
  deterministic hashes. ROM-free single-task and sequence gates are wired
  into CI, and an owned-local Mario sequence runner is in place; both native
  executions remain to be recorded before refactoring.**

### 4. Separate the RSP

- Keep graphics and audio task ownership behind `IN64GraphicsBackend` and
  `IN64AudioBackend`; both boundaries are now in place.
- Introduce an `IN64RspBackend` boundary for low-level task execution. **Complete.**
- Preserve the current HLE processors as the portable fallback. **Complete.**
- Add an LLE-capable backend only after task lifecycle, interrupts, and
  save-state ownership are explicit. **Instruction-level RSP scalar/vector core (`N64RspProcessor`) and `N64RspState` registers are now in place.**

### 5. Introduce per-game profiles

- Key settings by cartridge identity rather than filename. **Complete.**
- Limit profiles to verified compatibility choices such as backend, RSP mode,
  framebuffer emulation, and timing workarounds. **Complete.**
- Keep defaults correct and profiles auditable; avoid title-specific rendering
  hacks hidden inside generic code. **`N64GameProfileRegistry` is now active in `N64Machine`.**

### 6. Grow conformance and release evidence

- Run dump/replay fixtures in CI.
- Add longer scripted title routes for owned local cartridges.
- Track backend, unsupported commands, performance, audio integrity, and exact
  state replay in compatibility reports.
- Require manual play verification before upgrading a title from `PARTIAL`.

## Licensing boundary

RMG is GPL-3.0 and is an architectural reference, not a source of code for
PixelDeck's MIT-licensed core. Direct code reuse would require a deliberate
licensing decision. paraLLEl-RDP is MIT-licensed; PixelDeck pins the generated
standalone tree at revision
`388d70f5835b352d841d9d9e5a08c5de01470f41` (core revision
`1cecd042b2619bc505c12bfdc713808386f2b54d`) and preserves its MIT notice.
