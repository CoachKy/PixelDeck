# Pixel64 0.2 certification

Pixel64 0.2.003 is a graphics-and-input development milestone. It is not a
gameplay-compatible release.

## First cartridge target

- Super Mario 64 (USA), revision 0
- Internal game code: `NSME`
- Cartridge size: 8 MiB
- Header CRC pair: `635A2BFF / 8B022326`
- CIC: 6102
- Video region: NTSC

The local cartridge image is test input. It is ignored by Git and is never
included in source, published packages, logs, or generated certification
evidence.

## Passing gates

- The Pixel64 core test group contains 24 passing cartridge, CPU, memory,
  controller, persistence, scheduler, and graphics regressions.
- The R4300i path implements the single-precision `TRUNC.W.S` instruction used
  by the target and preserves the correct 32-bit FPR result format.
- CP0 indexed TLB writes now map paired pages. The target's 64 KiB Goddard
  dynlist mapping at virtual `0x04000000` resolves to RDRAM instead of being
  mistaken for physical RSP memory.
- FR=0 floating-point mode aliases doubles through even/odd 32-bit FPR pairs.
  This prevents the target's valid face-animation matrices from being
  misclassified as singular.
- SP task parsing uses the complete `OSTask` layout, including the real data
  pointer and size fields, and reports the task-done signal expected by
  libultra.
- The AI interface models its two-entry DMA FIFO, DAC-rate timing, busy/full
  status, and MI completion interrupts.
- MI mode writes acknowledge DP interrupts, allowing the game scheduler to
  leave the interrupt handler and continue producing frames.
- The initial Fast3D path resolves segmented display lists, transforms
  vertices, draws RGBA16/32 color targets, fills rectangles, rasterizes
  vertex-colored triangles, and samples RGBA16, RGBA32, IA8/16, and I4/8
  textures.
- Fast3D texture rectangles consume their paired half commands. `G_LOADBLOCK`
  copies source bytes into persistent 4 KiB TMEM instead of retaining a RDRAM
  pointer, and active tiles resolve their own TMEM slot.
- Preliminary depth comparison/update and homogeneous trivial clipping reject
  hidden and wholly offscreen fragments. Exact RDP coverage and depth encoding
  remain outside this milestone.
- SI DMA completes as a timed hardware event. Resident PIF controller commands
  run before every read DMA, and the libultra-compatible button mask delivers
  Start, A/B/Z, D-pad, C-buttons, and shoulders in the correct bit positions.
- A scripted Start press is observed by the real game-side controller structure
  and advances the target from its title sequence to a partially rendered file
  select with a visible hand cursor.
- A local 600-field diagnostic run advances 196 graphics tasks, 579 audio
  tasks, and 578 completed AI DMAs. With scripted title input it processes
  525,268 display-list commands, 492,930 transformed vertices, 78,272
  rasterized triangles after clipping/culling, and 499 texture rectangles
  without an unknown CPU or Fast3D opcode.

## Current visual result

The intended repeating title backdrop and the file-select hand cursor are now
visible in the VI framebuffer. Foreground composition, exact texture addressing,
combiner behavior, clipping, lighting, depth, blending, and framebuffer
presentation are not accurate yet.

## Explicitly outside this milestone

- Gameplay-ready Super Mario 64 output
- Exact RDP TMEM load, swizzle, combiner, blender, coverage, and depth behavior
- RSP vector microcode execution and audio synthesis
- Complete TLB exceptions, cache behavior, and cycle timing
- Correct VI scaling, filtering, serration, and overscan
- Controller Pak, Rumble Pak, Transfer Pak, and four-player dashboard mapping
- Compatibility claims for cartridges other than the first target

Pixel64 remains below 1.0 and the dashboard continues to mark its library
entries as partial.
