# Pixel64 architecture audit

## Status 2026-08-05

Written to answer two questions: why Mario Golf lags, and whether Pixel64 is
carrying too many competing designs at once.

---

## Bottom line

Pixel64 is not really a Frankenstein of two emulators. It is **one working
emulator (the Project64/GLideN64 model) with a second, complete, unused
emulator (the parallel model) built alongside it and never switched on.**

The parallel path is not a stub or a sketch. Real paraLLEl-RDP is compiled
(`native/pixeldeck_rdp/build/bin/pixeldeck_rdp.dll`, 2.2 MB), the C ABI bridge
is written, display-list lowering to native RDP packets is implemented, and
scanout is wired to come back from paraLLEl-RDP with its AA/divot/dither/gamma
filters. Then:

```csharp
public N64RdpBridgeMode RdpBridgeMode { get; set; } = N64RdpBridgeMode.Off;
```

and `TryEnableNativeRdp(...)` — the only thing that changes that — **has zero
callers** anywhere in `src/`, `tools/`, or `tests/`. Every launch logs
`N64 graphics backend: Pixel64 Fast3D software renderer`.

**Recommendation: finish and switch on the bridge. Keep HLE display-list
decode, hand rasterization to native paraLLEl-RDP.** Delete the managed
middle layer that duplicates paraLLEl-RDP badly. Rationale in
[Choosing a path](#choosing-a-path).

---

## What actually runs today

| Stage | Implementation | Lines | Live |
| --- | --- | ---: | --- |
| CPU | `Vr4300Cpu` interpreter + block cache | 2,364 | yes |
| RSP graphics | HLE decode in `Fast3dRenderer` + `Microcodes/` | 5,122 | yes |
| RSP audio | HLE decode in `N64AudioProcessor` | 2,352 | yes |
| RDP | `Fast3dRenderer` scalar software rasterizer | (above) | yes |
| Scanout | `N64Machine.RenderVideoInterface` | — | yes |

`N64Machine.ServiceRspTask` dispatches graphics and audio tasks straight to
`_graphicsBackend` / `_audioBackend`. That is the entire live path.

## What is built and dormant

| Component | Lines | State |
| --- | ---: | --- |
| `PdRdpNative` + `native/pixeldeck_rdp` | 196 + 131 C++ | Real paraLLEl-RDP, compiled, **never initialised** |
| `ParaLLElRdpEngine` | 442 | Managed LLE RDP; **cannot rasterize triangles** (`RdpTrianglesUnhandled`) |
| `VulkanComputePipeline` | 204 | Builds descriptor sets; **never creates a shader module or compute pipeline** |
| `VulkanRdpContext`, `VulkanCommandBufferQueue` | 453 | Support for the above |
| `N64RdpCommand/Trace/Replay/TriangleEncoder/StateSnapshot` | ~1,100 | Lowering + replay; only reachable via bridge modes |
| `N64RspProcessor` (LLE RSP) | 1,767 | **Never invoked by `ServiceRspTask`**; only exercised by the PeterLemon script (13/26 vector tests pass) |
| `N64FrameInspector` | 104 | **Zero callers anywhere** |

Roughly **4,200 lines of C# plus a 2.2 MB native library that never executes.**

## Two lineages, and what was actually inherited

Licensing is in order — PixelDeck is GPLv2 and `THIRD_PARTY_NOTICES.md`
attributes Dolphin, paraLLEl-RDP (pinned revision), and ares. Nothing here is
an undisclosed copy.

**From the Project64 / GLideN64 lineage** — conceptual, not copied. The
microcode CRC table in `N64GraphicsTaskProfile`, `gSPModifyVertex` semantics,
`G_MW_POINTS` decoding, the per-title profile registry. This is the model
Pixel64 actually implements.

**From the parallel lineage** — one genuine binary dependency (paraLLEl-RDP,
correctly pinned and attributed) plus a managed re-implementation of the same
idea that does not work.

The Frankenstein is not "Project64 parts mixed with parallel parts". It is
**`ParaLLElRdpEngine` + `VulkanComputePipeline`: a hand-written software/Vulkan
RDP sitting between two things that already work.** It duplicates what
paraLLEl-RDP does properly, and it is the layer whose doc comments overclaim
most ("exact pixel-accurate tile rasterization … matching ParaLLEl-RDP
specifications" above a method that counts triangles and drops them).

## Choosing a path

### Option A — Commit to HLE (the Project64 model)

Delete the whole parallel path, keep improving `Fast3dRenderer`.

- Simplest. Removes ~4,200 lines and the native build step.
- Keeps both current bottlenecks: the software rasterizer is 50–83% of frame
  time on texture-heavy games, and every title brings new decode bugs
  (Cruis'n `G_MW_POINTS`, Mario Golf `0xF1`, the `F3DBETA` enum mismatch).
- Accepts a permanent per-game bug tail. This is Project64's actual history.

### Option B — Commit to full LLE (the parallel model)

LLE RSP feeding native paraLLEl-RDP. Accuracy by construction, no per-game
graphics hacks ever again.

- Blocked on the RSP: 13 of 26 PeterLemon vector tests fail today.
- **Cost risk is real.** LLE RSP means interpreting every RSP instruction on
  top of a VR4300 interpreter that is *already* a bottleneck. mupen64plus's
  LLE RSP is far slower than its HLE path. Pixel64 cannot currently afford it.

### Option C — HLE decode → native paraLLEl-RDP rasterization ← **recommended**

Keep the 5,122 lines of display-list decode. Stop rasterizing in C#. Let
`N64RdpTriangleEncoder` lower primitives to real RDP packets and let
paraLLEl-RDP draw them on the GPU. This is exactly what `N64RdpBridgeMode`
was designed for and it is already wired end to end.

Why this one:

1. **The expensive half is already paid for.** Native paraLLEl-RDP is built,
   bridged, licensed, and pinned. Lowering is implemented. Scanout is wired.
2. **It deletes the #2 bottleneck outright.** Graphics time goes from 50–83%
   of the frame to approximately zero CPU cost.
3. **It deletes most of the bug class, not just individual bugs.** Nearly
   every rendering defect chased recently was a *rasterization* bug — TMEM
   swizzle, RGBA32 bank splitting, texture-rectangle edge rules, CI palette
   decoding, combiner behaviour. All of those become paraLLEl-RDP's problem,
   and it is cycle-accurate.
4. **It does not require a correct LLE RSP**, so it is not blocked behind the
   13 failing vector tests.

What it does *not* fix: display-list decode bugs (`G_MW_POINTS`, `0xF1`) stay
HLE bugs. That tail shrinks but does not vanish.

### Blocker found while starting the migration (2026-08-05)

`TryEnableNativeRdp` **blocked indefinitely** on the first attempt to bring
paraLLEl-RDP up headlessly: 10 minutes elapsed, 4 seconds of CPU, no return and
no output. The process could not be killed — neither `Stop-Process -Force` nor
`taskkill /F` terminated it, and it sat at a single thread holding its file
locks.

The block is inside `Vulkan::Context::init_instance_and_device`
(`native/pixeldeck_rdp/pdrdp.cpp:39`), which cannot be cancelled from managed
code. This may well be an artefact of a host with no usable Vulkan device
rather than a defect in the shim — but it means the call is **not safe to
invoke from a UI or emulation thread as written**, because a host that behaves
this way gets a frozen, unkillable emulator rather than a graceful fallback to
software.

**The first mitigation did not work, and was removed.** An in-process watchdog
(`Task.Run` + `Wait(timeout)`) was tried and *does not survive this failure*: a
test process printed nothing at all past the wait, 0.67s of CPU in ten minutes.
The stalled native initialiser holds the OS loader lock, so the thread waiting
on the timeout cannot make progress either. A managed deadline cannot be
enforced against it.

**What works: probe out-of-process.** `NativeRdpProbe` starts a throwaway host
with `--probe-parallel-rdp`, which calls `pdrdp_init` and exits 0 or 3. Only if
that host exits cleanly does the emulator load the native library itself. A
deadline can be enforced from outside, and the expendable process is abandoned
if it hangs. `PixelDeck.App` acts as its own probe host, before any window is
created.

Measured on the host where the in-process version hung forever:

    probe returned after 25.1s
      enabled     = False
      bridge mode = Off
      reason      = 'parallel-rdp did not initialise within 25s; ...'
      ran 300 fields afterwards: 320x237 frame=0D27E5F528B0FBAF

The emulator kept running normally afterwards. **A wedged probe host cannot
reliably be killed, though** -- one was cleaned up successfully, a later one
refused `taskkill /F` exactly like the in-process case. Containment is therefore
partial: the emulator survives, but the abandoned process persists until reboot
and keeps its file handles.

**Known flaw in the current wiring.** `PixelDeck.App` is its own probe host, so
a wedged probe holds handles on the app's own binaries -- enough to block
rebuilds, and enough to break the auto-updater in a release build. The probe
host should be a dedicated minimal executable in its own directory, sharing no
files with the app. Until that is done, treat `N64RdpBackend` as a developer
setting.

Use
`N64Machine.TryEnableNativeRdpSafely(probeHost, mode, upscaling, timeout)`;
the unguarded `TryEnableNativeRdp` remains only as the primitive it wraps.

Exclusive also now refuses to switch Fast3D off unless
`LleRdpEngine.IsNativeRdpActive`. Without a native device the managed fallback
cannot rasterize a triangle, so doing so traded a working picture for an empty
one.

**This needs to be re-tried on a machine with a real GPU before Option C can
finish.** Only the final flip to Exclusive is gated on it, though — see below.

### Correction: the lowering half needs no GPU (2026-08-05)

`RdpBridgeMode` is a settable property, so Mirror can be entered without
`TryEnableNativeRdp` and without any Vulkan device. The software raster keeps
the screen, Fast3D lowers its primitives into native RDP packets, and the
managed engine counts what arrives. All of the lowering work is measurable and
fixable on CPU alone.

**Blocker found and fixed the moment Mirror was actually exercised.** Mirror is
documented as "Output is unchanged, so this is the safe mode" — it was not.
With no native device, `ParaLLElRdpEngine.ExecuteCommands` falls through to the
managed fallback, whose `ExecuteFillRectangle` writes straight into RDRAM at the
colour image. Both rasterizers were drawing the same frame buffer, and the fill
rects won: every title that clears its frame buffer rendered **blank**.

    IDENTICAL  Super Mario 64        off=1C7E00D780F02825 mirror=1C7E00D780F02825
    DIFFERS    Turok                 off=59D0DFE63861D614 mirror=1C7E00D780F02825
    DIFFERS    Ocarina of Time       off=DD401D3D8D1BE0F1 mirror=1C7E00D780F02825
    DIFFERS    Quest 64              off=FA1703BFB916B6AD mirror=1C7E00D780F02825

`1C7E00D780F02825` is a blank frame — three unrelated titles collapsing onto one
hash is the tell. `ParaLLElRdpEngine.ManagedRasterizationEnabled` now gates the
fallback, and `N64Machine` clears it in Mirror. Exclusive keeps it, since there
it is the renderer of last resort. Re-checked: **8 of 8 titles identical**.

This mattered because Mirror is step one of the migration. It had never been
run: `TryEnableNativeRdp` sets the mode to `Off` on failure, so the broken path
was unreachable by the only public route into it.

### Lowering coverage, 600 fields per title

| Cartridge | Drawn | Lowered | Coverage |
| --- | ---: | ---: | ---: |
| Turok | 119,127 | 119,058 | 99.94% |
| Super Mario 64 | 139,799 | 137,014 | 98.01% |
| Mario Golf | 113,209 | 110,542 | 97.64% |
| Ocarina of Time | 70,222 | 68,245 | 97.18% |
| Doom 64 | 10,248 | 9,734 | 94.98% |
| Wave Race 64 | 144,680 | 133,765 | 92.46% |
| Pilotwings 64 | 592,537 | 468,427 | 79.05% |

The shortfall is **not** an encoder gap. `N64RdpTriangleEncoder.TryEncode` has
exactly two failure paths and both are `signedArea == 0` after quantising to
`SubpixelBits = 2` — the RDP's own s.11.2 triangle setup precision. Those are
triangles real hardware would also produce nothing for. What the numbers
actually show is Fast3D *over*-drawing: its float rasterizer accepts slivers
down to `|area| < 0.0001` in pixel units, far finer than the hardware grid.
Under Exclusive those slivers correctly disappear. Pilotwings is the extreme
because its terrain is dense and distant.

Two unrelated findings surfaced by the sweep:

- **Mario Kart 64 hits `G_LINE3D` (0xB5) 188,925 times** while drawing only
  1,882 triangles. It uses F3DEX 0.95, whose opcode map differs; 0xB5 is being
  misread. This is the open "renders no geometry" issue, now with a specific
  cause to chase.
- Quest 64 and Star Fox 64 draw zero triangles at 600 fields — they are still
  on 2D screens there, not a lowering problem.

Migration order:

1. Call `TryEnableNativeRdp(Mirror, 1, timeout)` from the app behind a setting,
   default off. Mirror keeps the software raster on screen, so nothing
   regresses while the packet stream is measured.
2. Drive down `OmittedForNoPerspective` / `OmittedUnsupportedPrimitive` and
   `RdpTrianglesUnhandled` until the lowered stream is complete.
3. Flip to `Exclusive`, re-record `tools/PixelDeck.N64Baseline`.
4. Delete `ParaLLElRdpEngine`'s managed rasterizer, `VulkanComputePipeline`,
   `VulkanCommandBufferQueue`, `VulkanRdpContext`.

## Cleanup independent of the path chosen

- Delete `N64FrameInspector` (104 lines, zero callers).
- Delete `VulkanComputePipeline` — it can never execute anything without a
  shader module, and paraLLEl-RDP brings its own pipeline.
- Fix the `N64GraphicsTaskProfile` CRC table: it returns `"F3DBETA"` and
  `"F3DEX"`, but the enum members are `F3dBeta` and `F3dex`.
  `Enum.TryParse(..., ignoreCase: false)` at `Fast3dRenderer.cs:248` fails and
  silently falls back to `Fast3d`, downgrading Wave Race 64, Shadows of the
  Empire and Power League. `ClassifyMicrocode` (line 741) uses `Enum.Parse`
  and would throw on the same strings.
- Rewrite doc comments that claim capabilities the code does not have
  (`ParaLLElRdpEngine`, `VulkanComputePipeline`, "100% LLE SIMD Hardware
  Engine" in `N64GameProfile.cs`).
- Count silently dropped *sub-commands*, not just unsupported opcodes. Every
  bare `return` in `MoveWord`, `MoveWordF3dex2` and `MoveMemory` is an
  invisible failure today — that is precisely how the Cruis'n USA bug survived.

---

## Mario Golf specifically

Not a rendering problem. **It never gets idle-skipped.**

PC sampling over 300 fields of gameplay:

```
samples=60000 executed=120,000,000 idle-skipped=0 (0.0%)

  0x80029E88   8.2%      0x80029EA0   8.2%      0x80029EB8   8.2%
  0x80029EBC   7.5%      0x80029E8C   7.5%      0x80029EA4   7.5%
  ...
```

~78% of all samples land in one 17-instruction window, `0x80029E80`–`0x80029EC4`:

```asm
80029E80  addiu $16, $zero, -1
80029E84  lui   $a0, 0x800C
80029E88  lw    $a0, -288($a0)     ; poll a flag in RDRAM
80029E8C  beq   $a0, $zero, 0x80029E9C
80029E90  nop
80029E94  jal   0x800AD3C0         ; only when the flag is set
80029E98  nop
80029E9C  lui   $v0, 0x800B
80029EA0  lbu   $v0, 0x67C8($v0)   ; poll a second flag
80029EA4  bne   $v0, $18, 0x80029EB4
```

That is a **memory-poll wait loop**. `Vr4300Cpu.TrySkipIdleLoop` only accepts
a single shape:

```csharp
var isSelfBranch = branch == 0x1000FFFFu;   // b .
...
delay != 0                                   // delay slot must be literally NOP
```

plus a requirement that interrupts be disabled. Mario Golf's loop is 17
instructions polling RDRAM, so it can never match — hence `idle-skipped = 0`
and **400,000 instructions/field**, against Conker's 17,469/field.

This is the same root cause behind every zero-idle-skip title: NHL 99, Mario
Golf, Harvest Moon, Gauntlet Legends, StarCraft 64, Banjo-Kazooie, Mario
Tennis. All of them sit between 6.5 ms and 18.4 ms per field. Every game that
*does* get idle-skipping sits between 0.9 ms and 4 ms.

### Implemented 2026-08-05

`Vr4300Cpu.DynamicIdleDetectionEnabled` (default on). A wait loop is recognised
when execution returns to the same address with a byte-identical GPR/FPR/HI/LO
fingerprint, no store retired, and no read from outside RDRAM, having actually
retired instructions in between.

Every one of those conditions was needed. Two earlier versions looked like wins
and were not:

- Matching only against the *previous* probe fails, because a wait loop spans
  several cached blocks and consecutive probes land on different addresses. An
  8-slot table keyed by address fixed it.
- Requiring only "same state" made the skip self-confirming: after a skip no
  instruction has run, so state is trivially unchanged and authorises the next
  skip forever. Games reported 100% idle at 0.05 ms/field while *not
  executing*. Hence the retired-instruction requirement.
- Ignoring where loads came from let loops polling a free-running counter (VI
  current line) qualify: they read the same value twice and look like a fixed
  point while remaining sensitive to sampling time. That perturbed 9 titles;
  restricting idle loops to RDRAM-only access brought it to 5.

Measured over 600 fields:

| Cartridge | Before | After |
| --- | ---: | ---: |
| Mario Tennis | 13.55 ms (0% skipped) | 9.06 ms (88%) |
| Mario Golf | 16.18 ms (0%) | 12.73 ms (70%) |
| Harvest Moon 64 | 12.83 ms (0%) | 10.14 ms (60%) |
| Gauntlet Legends | 11.70 ms (89%) | 10.79 ms (89%) |
| Super Mario 64 | 9.83 ms (62%) | 9.86 ms (64%) |
| NHL 99 | 22.37 ms (0%) | 21.62 ms (0%) |

Baselines: **29 match, 6 changed**. Wave Race 64 is the microcode-table fix.
The other five — Cruis'n USA, Harvest Moon 64, Mario Golf, Mario Tennis, Mega
Man 64 — are the titles that newly gain skipping, and they shift because the
following interrupt lands on a different cycle. All five were rendered at the
baseline field count and check out; Mega Man 64's grid seam is present with the
detector disabled too, so it is pre-existing. Re-record baselines to adopt.

NHL 99 still gains nothing: its wait loop is not a fixed point by this test.

### The original diagnosis

Replace the single-pattern matcher with a **dynamic idle detector**: when
execution returns to the same PC with identical architectural state (GPRs, CP0
Count aside) and no store has retired since the previous visit, nothing but an
interrupt can change the outcome — so advance to `TicksUntilNextCpuEvent`.

That handles poll loops of any shape without a per-game table, and it keeps
the existing safety property of stopping at the next device or timer event.
The `b .; nop` case falls out as a trivial instance.

Expected on Mario Golf: CPU is 48.2% of a 15.41 ms field, and ~78% of that is
the poll — roughly **15.4 ms → 9–10 ms**. The remaining 50% is the software
rasterizer, which is what Option C removes.
