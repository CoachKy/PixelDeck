# PixelDeck v1.26.077

PixelDeck 1.26.077 advances Pixel64 to 0.13.018 with native RDP triangle
lowering and a fail-safe live paraLLEl-RDP validation route. The dashboard and
the default software-rendered gameplay path are unchanged.

## Pixel64 0.13.018

- Lowers clipped Fast3D, F3DEX, and F3DEX2 geometry into native 44-word
  shade/texture/depth triangle packets.
- Encodes RDP edge slopes, RGBA gradients, perspective texture coefficients,
  depth gradients, texture tile, and mip level using the pinned MIT-licensed
  paraLLEl-RDP equations.
- Produces complete native command traces for verified ordinary triangle tasks
  rather than marking all HLE geometry omitted.
- Adds a real Super Mario 64 gate which finds a triangle-producing task and
  requires complete native packets with no unsupported source commands.
- Adds an opt-in live bridge selected with
  `PIXELDECK_N64_VIDEO_BACKEND=parallel-rdp`.
- Executes the software renderer first and commits native RDRAM only after
  complete, successful paraLLEl-RDP execution.
- Falls back for missing Vulkan/native support and permanently disables the
  native route after any incomplete or failed task to prevent state drift.
- Reports an opcode histogram when exporting `.p64rdp` traces.

## Still open

The live native route remains a developer validation switch. Compiled Windows
and Linux CI artifacts must run captured Mario triangle tasks through Vulkan
with framebuffer/depth/coverage assertions before paraLLEl-RDP becomes a
dashboard setting or default.
