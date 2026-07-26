# Pixel64 0.3 certification

Pixel64 0.3.005 is a controllable-gameplay development milestone. It is not a
graphics-accurate or generally compatible Nintendo 64 release.

## First cartridge target

- Super Mario 64 (USA), revision 0
- Internal game code: `NSME`
- Cartridge size: 8 MiB
- Header CRC pair: `635A2BFF / 8B022326`
- CIC: 6102
- Video region: NTSC

The local cartridge image is test input. It is ignored by Git and is never
included in source, published packages, logs, screenshots, or generated
certification evidence.

## Passing gates

- The focused Pixel64 group contains 27 passing cartridge, CPU, memory,
  controller, persistence, scheduler, and graphics regressions.
- The R4300i path executes the first target through its title, file-select,
  Peach/Lakitu introduction, and castle-grounds transition.
- CP0 paired TLB mappings and FR=0 floating-point register aliases support the
  target's 64 KiB Goddard dynlist mapping and animation matrices.
- SP task parsing, MI interrupt acknowledgement, the AI two-entry DMA FIFO, and
  timed SI/PIF controller reads keep the libultra scheduler advancing.
- The Fast3D path resolves segmented display lists, transforms vertices,
  rasterizes vertex-colored and textured triangles, draws texture rectangles,
  and supports the target's current RGBA, intensity, and
  intensity-alpha texture formats.
- `G_LOADBLOCK` data persists in 4 KiB TMEM and active tiles resolve their TMEM
  slots.
- Polygons are clipped against the canonical homogeneous view volume before
  perspective division. The target trace's largest projected triangle is now
  322 by 242 pixels instead of producing runaway screen-spanning coordinates.
- `G_SETOTHERMODE_L` preserves unchanged mode bits. Depth comparison and depth
  writes follow the RDP `Z_CMP` and `Z_UPD` render-mode flags instead of being
  forced for every geometry-Z-buffered primitive.
- Positive clip-space Y maps toward the top of the N64 framebuffer. Front/back
  culling uses the corresponding top-left framebuffer winding, keeping the
  corrected geometry visible instead of discarding it.
- Correct N64 button masks and repeated PIF command execution deliver Start,
  A/B/Z, D-pad, C-buttons, shoulders, and analog-stick magnitude to the real
  game-side controller structure.
- The scripted gate waits for the title to become interactive, presses Start,
  selects Mario A, advances the opening dialog, reaches area 1, and observes
  Mario leave `ACT_INTRO_CUTSCENE`.
- The gate then holds analog-stick Y at 60 for 120 fields. Mario moves from
  approximately `(-1328, 260, 4354)` to `(-1298, 335, 3134)`, proving that the
  running game consumes live controller input.
- The completed 3,924-field trace advances 1,840 graphics tasks, 3,893 audio
  tasks, and 3,892 AI DMAs. It processes 4,970,569 display-list commands,
  4,072,262 transformed vertices, 627,508 rasterized triangles, 6,428 texture
  rectangles, and 242,714,497 textured pixel writes without an unknown CPU or
  Fast3D opcode.

## Current visual result

The title, file select, intro, and castle grounds are upright and recognizable,
and the game is controllable. The castle framebuffer is not yet
presentation-correct:

- Some texture rows, TMEM swizzles, and addressing are wrong.
- Ground and background composition can cover the wrong portions of the frame.
- Lighting and combiner behavior can render Mario as a white silhouette.
- Blender, coverage, depth encoding, and VI filtering remain approximate.

This milestone certifies execution and input progress, not a polished gameplay
experience.

## Explicitly outside this milestone

- Graphics-accurate Super Mario 64 output
- Compatibility claims for cartridges other than the first target
- Exact RDP TMEM load/swizzle, combiner, blender, coverage, and depth behavior
- RSP vector microcode execution and audio synthesis
- Complete TLB exceptions, cache behavior, and cycle timing
- Correct VI scaling, filtering, serration, overscan, and interlace
- Controller Pak, Rumble Pak, Transfer Pak, and four-player dashboard mapping

Pixel64 remains below 1.0 and the dashboard continues to mark its Nintendo 64
library entries as `PARTIAL`.
