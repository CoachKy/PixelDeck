# PixelDeck v1.27.078

PixelDeck 1.27.078 advances Pixel64 to 0.14.019 with native framebuffer,
depth-buffer, and hidden-coverage validation. Dashboard visuals and the
default software-rendered gameplay path are unchanged.

## Pixel64 0.14.019

- Advances the optional paraLLEl-RDP bridge to ABI 2.
- Adds checked upload and readback for the RDP's real 4 MiB hidden-coverage
  memory.
- Detects framebuffer and depth-image regions from captured native commands.
- Reports input/output hashes and exact changed-byte counts for RDRAM,
  framebuffer, depth, and hidden coverage.
- Extends repeat replay checks to include every native output rather than only
  the full RDRAM hash.
- Adds a ROM-free shaded-triangle scene that must modify color, depth, and
  coverage deterministically.
- Configures Linux CI with a CPU Vulkan implementation and makes that native
  replay a required gate.
- Keeps Windows compilation, ABI/export validation, and safe software
  fallback coverage.

## Still open

The native route remains an opt-in developer validation switch. Selected
real-game framebuffer/depth/coverage assertions, multi-task hidden-state
continuity, texture/combiner triage, VI timing, and physical Windows/Raspberry
Pi validation remain before paraLLEl-RDP can become a normal dashboard option.
