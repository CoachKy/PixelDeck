# Pixel64 0.13 certification

Pixel64 0.13.018 is the native HLE-triangle lowering milestone. It advances
the pinned paraLLEl-RDP integration from state/rectangle replay to complete
ordinary Fast3D/F3DEX/F3DEX2 triangle packets while retaining Pixel64's
software renderer as the default.

- Product versions report Pixel64 `0.13.018` and PixelDeck `1.26.077`.
- Dashboard visuals, libraries, controller ports, saves, and pause UI are
  unchanged.
- Super Mario 64 (USA) revision 0 remains the only certified gameplay route.
- The native backend is opt-in while its output is compared with captured
  software frames.

## Triangle lowering

The managed `N64RdpTriangleEncoder` converts clipped screen-space vertices into
the RDP's 44-word `ShadeTextureZBufferTriangle` packet:

- 10.2-subpixel Y and 16.16 X edge setup;
- major, upper, and lower edge slopes with RDP-compatible rounding;
- 16.16 RGBA values and X/edge/Y derivatives;
- perspective-scaled S, T, and reciprocal W coefficients;
- normalized Z plus X/edge/Y derivatives; and
- tile and maximum mip level from the active Fast3D texture state.

The equations and packet layout follow the MIT-licensed `triangle_converter`
and `rdp_command_builder` in pinned paraLLEl-RDP core revision
`1cecd042b2619bc505c12bfdc713808386f2b54d`. PixelDeck preserves the upstream
notice in `THIRD_PARTY_NOTICES.md`.

Clipped-away and degenerate triangles do not make a trace incomplete because
neither backend draws them. HLE lines and incomplete custom-microcode
generators still do. Unsupported-source accounting is measured per capture
rather than leaking a renderer's lifetime total into later tasks.

## Live opt-in with automatic fallback

When the tested native runtime is installed, developers can select the live
bridge before starting PixelDeck:

```powershell
$env:PIXELDECK_N64_VIDEO_BACKEND = 'parallel-rdp'
```

For each eligible graphics task, Pixel64:

1. preserves the exact pre-task RDRAM;
2. executes the software renderer as a valid fallback;
3. captures the ordered native RDP stream, including lowered triangles;
4. replays a complete stream against the pre-task RDRAM through paraLLEl-RDP;
5. commits native RDRAM only after successful synchronized readback.

If the bridge is absent, Vulkan is unsupported, a task contains unlowered work,
or native execution fails, Pixel64 keeps the software result. After an
incomplete or failed task it disables native rendering for that session so
the two backends cannot silently diverge in TMEM or RDP state.

This is a validation switch, not the release default.

## Proven gates

The synthetic encoder gates verify packet length/opcode, edge Y fields,
tile/mip fields, RGBA coefficient packing, zero derivatives for constant
attributes, depth gradients, and degenerate rejection.

The local Super Mario 64 gate boots the real cartridge, finds a graphics task
that draws triangles, lowers it again from its exact `.p64gfx` capture, and
requires:

- at least one 44-word opcode `0x0F` packet;
- zero omitted HLE primitives;
- zero unsupported source commands; and
- a complete `.p64rdp` task.

The first boot graphics task is also exported deterministically. It contains
41 genuine state/fill/sync/image packets with no omissions or unsupported
source commands; that particular early task does not yet contain geometry.

## Remaining gate before default use

Pixel64 still needs a compiled native CI artifact to run the same Mario
triangle task through paraLLEl-RDP and compare framebuffer, depth, and hidden
coverage against known assertions. Texture perspective, combiner/blender
state, VI scanout, line lowering, custom microcode, and multi-task state
continuity then need focused mismatch triage.

Only after those comparisons pass should the native renderer become a normal
dashboard setting or an automatic default. Other Nintendo 64 games remain
experimental attempts.

## Reproducing managed gates

```powershell
dotnet test tests/PixelDeck.App.Tests/PixelDeck.App.Tests.csproj `
  --filter FullyQualifiedName~N64RdpTriangleEncoderTests

dotnet test tests/PixelDeck.App.Tests/PixelDeck.App.Tests.csproj `
  --filter FullyQualifiedName~LocalSuperMario64TriangleTaskLowersToCompleteNativePacketsWhenPresent

dotnet build PixelDeck.sln --no-restore
```
