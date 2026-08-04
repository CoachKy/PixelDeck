# Pixel64 accuracy remediation plan

## Status as of 2026-08-04

| Phase | State |
| --- | --- |
| 0.1 External oracle | **Done** via PeterLemon RSP suite, not `n64-systemtest` |
| 0.2 Golden-hash baselines | **Not started** |
| 0.3 Delete self-referential tests | **Done** (with phase 2) |
| 1 Cheap fixes | **Done** |
| 2 RSP LLE core | **Done**, with caveats listed in 2.3 and 2.6 |
| 3 VI resampling | **Done** (fixed 640x480 raster) |
| 4 RDP depth/LOD | **Not started** |
| 5 Backend decision | **Not started** |

Test suite: 194 passing, 1 failing. The failure
(`LocalRogueSquadronUsesItsDedicatedFactor5CommandStreamWhenPresent`) predates
this work and comes from `Fast3dRenderer.F5Rogue.cs` being deleted in the
working tree.

### The oracle exists now

`n64-systemtest` publishes no prebuilt ROM and needs a Rust toolchain plus
`nust64` to build, so Phase 0.1 was satisfied instead with the
[PeterLemon/N64](https://github.com/PeterLemon/N64) `RSPTest/CP2` suite, which
ships prebuilt ROMs *and* a hardware reference screenshot per test. Run it with
`.\scripts\Test-Pixel64Rsp.ps1`.

First result, 2026-08-04, against the Phase 2 rewrite:

- **13 pass**: VMULF, VMUDL, VMUDN, VMADL, VMADN, VAND, VOR, VXOR, VABS, VSAR,
  VNOP, VRCPH, TransposeMatrixVMOV
- **13 fail**: LTV, LWV, TransposeMatrix, VADD, VSUB, VEQ, VLT, VCL, VCR,
  VMACF (VMACU rows only), VRCP, VRCPL, SORT
- **12 undocumented-opcode ROMs** fail as expected; those instructions are
  deliberately unimplemented.

The failures land almost exactly on the areas flagged as unverified in section
2.6 — LTV, VCL/VCR and the reciprocal ROM — which is the intended behaviour of
an oracle. Clearing VCO after VADD/VSUB was tried as a fix for those two and
did **not** change the result, so the cause is elsewhere.

`n64-systemtest` remains worth adding later for CPU/COP0/TLB coverage, which
this suite does not touch.


This plan addresses the findings from the 2026-08-04 review. It is ordered so
that each phase makes the next one measurable. Phase 0 is a hard prerequisite:
until there is an external oracle, no later change can be scored, and the
project will keep spending effort without observable improvement.

## Governing principle

Every table in the RCP (opcodes, command lengths, register layouts) must be
derived from a primary reference and cited in a comment next to the code. The
review found three separate tables written from recall — the RSP COP2 vector
map, the RDP triangle command lengths, and the RDP debug opcode names — and all
three were wrong. Where a value below is marked **VERIFY**, confirm it against a
primary source during implementation rather than trusting this document.

References used here:

- `n64-systemtest` — https://github.com/lemmy-64/n64-systemtest (self-reporting
  pass/fail; no image comparison needed)
- angrylion-rdp-plus `rdp_commands` table — https://github.com/ata4/angrylion-rdp-plus
- r64emu RSP documentation — https://github.com/rasky/r64emu/blob/master/doc/rsp.md
- emudev.org RSP encoding notes — https://emudev.org/2020/03/28/RSP
- N64brew wiki (403s to automated fetch; open in a browser) — https://n64brew.dev/wiki/

---

## Phase 0 — Build the oracle

**Nothing else should start before this lands.** Effort: M (2–3 sessions).

### 0.1 Integrate `n64-systemtest`

`n64-systemtest` is a homebrew ROM that decides its own pass/fail and prints
results — no golden images, no manual inspection. It covers the VR4300 core,
COP0, COP1, TLB, exceptions, and RSP behaviour.

- Add `tools/PixelDeck.N64SystemTest` (or extend `tools/PixelDeck.N64Compatibility`
  with a `--systemtest` mode) that boots the ROM headlessly, scrapes the
  pass/fail output, and emits a machine-readable summary.
- Commit the *result baseline* (counts per test group), not the ROM.
- Wire it into CI as a ratchet: the pass count may never decrease.

Deliverable: a single number that moves when the CPU/RSP gets better.

### 0.2 Give the compatibility lab a correctness signal

Today `CompatibilityClassifier.Classify` returns `Warning` for every cartridge
that is not Super Mario 64 rev 0, because
[`N64Cartridge.IsPixel64VerifiedTarget`](../src/PixelDeck.Emulation.N64/N64Cartridge.cs#L216)
hardcodes a one-title allowlist. That is why every run reports `Pass: 0`.

- Replace the allowlist with a per-title expectation file
  (`docs/pixel64-baselines/<cartridge-code>.json`) holding: field counts at
  which to sample, the SHA-256 of the VI output at each sample, and a short
  human note on what should be on screen.
- Commit hashes only — no ROM bytes, no framebuffer images. This sidesteps the
  asset-distribution constraint the lab already documents.
- Classification becomes: hash matches baseline → `Pass`; hash differs →
  `Regressed`; no baseline recorded → `Unbaselined` (not `Warning`).
- Seed baselines from the current output for the titles that already render
  correctly (Pilotwings 64, Zelda OoT file select). Those become the regression
  net that protects Phases 1–4.

Deliverable: a run that distinguishes "this got better", "this got worse", and
"we never checked".

### 0.3 Delete tests that encode the implementation

[`N64RspVectorTests.cs`](../tests/PixelDeck.App.Tests/N64RspVectorTests.cs)
asserts VADD at funct `0x08` and VAND at funct `0x19`. Both are wrong, and the
tests currently *protect* the bug. Remove them in the same commit that fixes the
opcode table (Phase 2) so the tree never has passing tests asserting known-false
behaviour. Audit the rest of the N64 test files for the same pattern before
trusting any of them as a baseline.

---

## Phase 1 — Cheap fixes with immediate payoff

These are small, independently verifiable, and safe to do as soon as Phase 0.2
gives a regression net. Effort: S (1 session total).

### 1.1 RDP triangle command lengths

[`Fast3dRenderer.cs:586`](../src/PixelDeck.Emulation.N64/Fast3dRenderer.cs#L586).
Four of eight are wrong, which desynchronizes the entire direct-RDP command
stream from the first occurrence onward.

Verified against angrylion's `rdp_commands` table (bytes):

| Opcode | Variant | Correct bytes | Correct 32-bit words | Current code |
| --- | --- | ---: | ---: | ---: |
| 0x08 | tri | 32 | 8 | 8 |
| 0x09 | tri + Z | 48 | 12 | **16** |
| 0x0A | tri + tex | 96 | 24 | 24 |
| 0x0B | tri + tex + Z | 112 | 28 | **32** |
| 0x0C | tri + shade | 96 | 24 | **20** |
| 0x0D | tri + shade + Z | 112 | 28 | 28 |
| 0x0E | tri + shade + tex | 160 | 40 | **36** |
| 0x0F | tri + shade + tex + Z | 176 | 44 | 44 |

Component sizes: edge 32 B, Z +16 B, texture +64 B, shade +64 B. The current
code assumes Z = +32 B and shade = +48 B. `0x0F` is correct only because those
two errors cancel — which is exactly why it is the one variant with test
coverage.

Add a test that round-trips all eight lengths, not just `0x0F`.

### 1.2 RDP debug opcode names

[`N64FrameInspector.cs:69`](../src/PixelDeck.Emulation.N64/N64FrameInspector.cs#L69)
is off by one: `0x2D` is SET_SCISSOR, `0x2E` SET_PRIM_DEPTH, `0x2F`
SET_OTHER_MODES, `0x30` LOAD_TLUT. Cosmetic, but fix it while the reference is
open — and cite the source in a comment.

### 1.3 SP DMA count/skip

[`N64RspProcessor.cs:419`](../src/PixelDeck.Emulation.N64/N64RspProcessor.cs#L419)
treats `SP_RD_LEN`/`SP_WR_LEN` as a flat length. Hardware splits the register
into length (11:0), count (19:12), and skip (31:20) for strided multi-line
transfers. Implement the strided form; length is `(len & 0xFFF) + 1` rounded up
to 8 bytes. **VERIFY** the rounding rule against a primary source.

---

## Phase 2 — Rewrite the RSP LLE core

The instruction-level RSP cannot execute any real microcode. Three defects
compound; fix them as one unit, not incrementally, because they interact.
Effort: L (3–5 sessions).

### 2.1 Correct the COP2 vector opcode table

Replace the table at
[`N64RspProcessor.cs:471`](../src/PixelDeck.Emulation.N64/N64RspProcessor.cs#L471).
Confirmed encoding (funct = instruction & 0x3F):

| funct | Op | funct | Op | funct | Op | funct | Op |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 0x00 | VMULF | 0x10 | VADD | 0x20 | VLT | 0x30 | VRCP |
| 0x01 | VMULU | 0x11 | VSUB | 0x21 | VEQ | 0x31 | VRCPL |
| 0x02 | VRNDP | 0x13 | VABS | 0x22 | VNE | 0x32 | VRCPH |
| 0x03 | VMULQ | 0x14 | VADDC | 0x23 | VGE | 0x33 | VMOV |
| 0x04 | VMUDL | 0x15 | VSUBC | 0x24 | VCL | 0x34 | VRSQ |
| 0x05 | VMUDM | 0x1D | VSAR | 0x25 | VCH | 0x35 | VRSQL |
| 0x06 | VMUDN | | | 0x26 | VCR | 0x36 | VRSQH |
| 0x07 | VMUDH | | | 0x27 | VMRG | 0x37 | VNOP |
| 0x08 | VMACF | | | 0x28 | VAND | | |
| 0x09 | VMACU | | | 0x29 | VNAND | | |
| 0x0A | VRNDN | | | 0x2A | VOR | | |
| 0x0B | VMACQ | | | 0x2B | VNOR | | |
| 0x0C | VMADL | | | 0x2C | VXOR | | |
| 0x0D | VMADM | | | 0x2D | VNXOR | | |
| 0x0E | VMADN | | | | | | |
| 0x0F | VMADH | | | | | | |

For comparison, the current code maps 0x08→VADD, 0x09→VSUB, 0x0D→VADDC,
0x10→VSAR, 0x11→VLT, 0x19→VAND, 0x1D→VXOR, 0x20→VRCP, 0x23→VMOV. It reads like
names assigned in compacted sequential order rather than hardware encoding.

### 2.2 Collapse the two accumulator conventions

`ExecuteVectorOp` has a fast path for `elementSpecifier == 0` and a general
path, and they disagree by a factor of 65536: the fast path stores `prod >> 16`
into the accumulator, the general path stores the full product. VMACF exists
only in the fast path, so VMACF with any element specifier is currently a no-op.

- Delete the fast path entirely. Implement one correct 48-bit accumulator path.
- Reintroduce a SIMD fast path later only if profiling demands it, and only
  behind a differential test that runs both paths and asserts equality.
- Also fix: VADD/VSUB in the fast path use `Vector128.Add`/`Subtract`, which
  wrap instead of clamping and ignore the VCO carry/borrow entirely.

### 2.3 Implement the vector load/store sub-opcodes

[`N64RspProcessor.cs:826`](../src/PixelDeck.Emulation.N64/N64RspProcessor.cs#L826)
treats every LWC2 as LQV and every SWC2 as SQV. The sub-opcode field is never
read, the element comes from the wrong bits, and the offset is decoded as a
16-bit immediate.

Correct encoding: `opcode 31..26 | base 25..21 | vt 20..16 | sub-opcode 15..11 |
element 10..7 | offset 6..0`. The offset is **7-bit signed, scaled by access
size** (1 for LBV, 2 for LSV, 4 for LLV, 8 for LDV, 16 for quad forms).

Sub-opcodes: 0x00 LBV/SBV, 0x01 LSV/SSV, 0x02 LLV/SLV, 0x03 LDV/SDV,
0x04 LQV/SQV, 0x05 LRV/SRV, 0x06 LPV/SPV, 0x07 LUV/SUV, 0x08 LHV/SHV,
0x09 LFV/SFV, 0x0A LWV/SWV, 0x0B LTV/STV.

**VERIFY** 0x08–0x0B against a primary source: one secondary summary consulted
during review listed 0x08 as LTV, which conflicts with the above. LTV is the
transpose load F3DEX2 uses for vertex loading, so getting it wrong is fatal to
the whole phase.

### 2.6 Known-unverified areas after the rewrite

The rewrite landed, but three things in it are not confirmed against hardware
and should be treated as suspect the moment `n64-systemtest` runs:

- **LTV / STV** (`N64RspProcessor.ExecuteVectorMemory`, sub-opcode 0x0B). The
  rotation direction and the element/row-offset interaction are written from a
  consensus reading, not a primary source. This is the vertex-load path for
  F3DEX2, so an error here is high impact.
- **VCL / VCR**. The clipping compares depend on carry, not-equal and
  compare-extension state carried from a preceding VCH. The algorithm is the
  standard one but has not been differentially tested.
- **Reciprocal ROM**. `N64RspState` generates the 512-entry rcp/rsq tables
  analytically instead of transcribing the hardware ROM, so VRCP/VRSQ results
  are close but not bit-exact.

VRNDP, VRNDN, VMULQ and VMACQ are deliberately unimplemented; they increment
`N64RspProcessor.UnimplementedVectorOps` rather than writing a wrong value.

### 2.4 Gate on the oracle

Run `n64-systemtest`'s RSP groups. Do not declare this phase done on the basis
of the code existing — that is precisely the error the current roadmap makes,
where milestone 4 is marked **Complete** for a core that cannot execute a single
real microcode instruction.

### 2.5 Correct the roadmap

Update `docs/PIXEL64-RMG-ROADMAP.md` milestone 4 to reflect behaviour rather
than code presence, and add a rule that milestones may only be marked complete
when an oracle confirms them.

---

## Phase 3 — Video interface

Effort: M (1–2 sessions). Highly visible; do it early for morale and because it
changes every captured baseline (so it must land before Phase 0.2 baselines are
considered stable, or plan to re-baseline once).

Currently [`N64Machine.cs:517`](../src/PixelDeck.Emulation.N64/N64Machine.cs#L517)
blits the framebuffer 1:1 and
[`N64Memory.cs:257`](../src/PixelDeck.Emulation.N64/N64Memory.cs#L257) *derives
the output surface size* from the VI window registers. Observed consequences in
the current captures: 320×237 for most titles, 440×325 for GoldenEye (VI_WIDTH
stride mistaken for display width), 292×213 for Banjo-Kazooie.

- Emit a **fixed** output raster (640×480 recommended) regardless of VI state.
- Resample the framebuffer into it through `VI_X_SCALE` / `VI_Y_SCALE` with the
  correct subpixel offsets, instead of indexing source pixels 1:1.
- Treat `VI_WIDTH` strictly as stride; derive display width from the H_VIDEO
  window and X_SCALE.
- Then add, in order: AA filter, divot filter, gamma, dither. Each behind a flag
  so baselines can be re-taken one step at a time.
- Interlacing last.

Verification: aspect ratio correct, no bottom-row loss, output size stable
across scene changes within a title.

---

## Phase 4 — RDP depth and LOD fidelity

Effort: L (3–5 sessions). This is the largest remaining correctness gap in the
bundled renderer, and it constrains how good Fast3D can ever get.

### 4.1 Move the depth buffer into RDRAM

[`Fast3dRenderer.cs:41`](../src/PixelDeck.Emulation.N64/Fast3dRenderer.cs#L41)
keeps depth in a host-side `float[]` private to the renderer. This breaks any
title that reads Z back, reuses the Z buffer as a colour buffer (common), or
depends on the N64's nonlinear Z encoding.

- Back depth with the game's `SET_MASK_IMAGE` (0x3E) allocation in RDRAM.
- Store in the hardware format: 14-bit mantissa + 3-bit exponent, with the
  hidden-coverage bits kept alongside. **VERIFY** the exact encoding against
  angrylion's `zbuffer.c` before implementing.
- Expect this to change output subtly across most titles — do it right after a
  baseline refresh, not before.

### 4.2 Implement LOD

[`Fast3dRenderer.cs:2923`](../src/PixelDeck.Emulation.N64/Fast3dRenderer.cs#L2923)
hardcodes `LodFraction => 0f`, so every LOD-driven combiner is wrong. Implement
real LOD computation and mip level selection.

### 4.3 Reconsider supersampling

`SuperSamplingRatio = 2` is not a hardware behaviour and changes coverage/AA
semantics away from the RDP. Once Phase 3 supplies real VI antialiasing, this
should be re-evaluated and most likely removed — it is currently compensating
for the missing VI filter in a way that will double-count.

### 4.4 Investigate the Mystical Ninja banding

The vertical red/green/black bands filling the background in the Goemon capture
are the signature of a framebuffer or texture sampled with the wrong
format/stride. Likely to be partly resolved by 4.1; if not, it is a focused
texture-format bug worth isolating with the existing `.p64gfx` capture/replay
path.

---

## Phase 5 — Backend decision

Effort: M, but only meaningful after Phases 0–4.

Only once the oracle exists and Fast3D is measured can you rationally decide
whether to keep deepening the software renderer or promote paraLLEl-RDP to the
default. Attempting this decision now is guesswork — the roadmap already notes
the comparison "must still be executed", and there is currently no metric that
would settle it.

- Run the full baseline suite through both backends.
- Compare against the Phase 0.2 hashes.
- Promote whichever wins per-title via `N64GameProfile`, not globally.

---

## Sequencing summary

| Phase | Blocks | Effort | Visible result |
| --- | --- | --- | --- |
| 0 Oracle | everything | M | A number that moves |
| 1 Cheap fixes | — | S | Direct-RDP streams stop desyncing |
| 2 RSP LLE | 5 | L | LLE path becomes usable at all |
| 3 VI | baselines | M | Correct aspect, stable output size |
| 4 RDP depth/LOD | 5 | L | Z-reads, render-to-texture, mipmaps |
| 5 Backend choice | — | M | Evidence-based default |

Phases 1, 2, and 3 are mutually independent once Phase 0 lands and can be done
in any order or in parallel. Phase 4 should follow Phase 3 so baselines are only
invalidated once.

## What "done" means

Each phase closes when the oracle says so, not when the code exists:

- Phase 0: CI reports a `n64-systemtest` pass count and a per-title baseline
  diff on every run.
- Phase 1: all eight triangle lengths covered by test; SP DMA strided transfers
  covered by test.
- Phase 2: `n64-systemtest` RSP groups pass; LLE can run a real F3DEX2 task to
  completion.
- Phase 3: baseline captures are 640×480 with correct aspect across all titles.
- Phase 4: at least one Z-read-dependent effect renders correctly.
- Phase 5: a documented per-title backend assignment backed by hash comparison.
