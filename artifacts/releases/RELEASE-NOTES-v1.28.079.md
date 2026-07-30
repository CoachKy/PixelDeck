# PixelDeck v1.28.079

PixelDeck 1.28.079 advances Pixel64 to 0.15.020 with multi-task native RDP
certification. Dashboard visuals and the default software renderer are
unchanged.

## Pixel64 0.15.020

- Replays ordered graphics traces through one paraLLEl-RDP context.
- Uploads exact per-task RDRAM while preserving hidden coverage between tasks.
- Proves the hidden-output-to-hidden-input hash chain explicitly.
- Extends ROM-free native CI with two non-overlapping sequential triangles.
- Requires deterministic framebuffer, depth, and hidden-coverage changes
  across fresh sequence replays.
- Adds an opt-in owned-local Super Mario 64 sequence certification test.
- Adds `scripts/Test-Pixel64ParallelRdp.ps1` for a downloaded ABI-2 artifact
  or local C++ build.
- Keeps every ROM-derived capture and output in memory and logs hashes only.

## Next

Run the native CI artifact and owned-local Mario sequence gate. Once those
pass, Pixel64 can begin its planned module-level refactor with this behavior
locked by deterministic tests.
