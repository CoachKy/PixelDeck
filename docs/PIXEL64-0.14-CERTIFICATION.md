# Pixel64 0.14 certification

Pixel64 0.14.019 is the native-output observability and certification-gate
milestone. It extends the PixelDeck paraLLEl-RDP bridge to ABI 2 without
changing dashboard visuals or making the native renderer the gameplay
default.

- Product versions report Pixel64 `0.14.019` and PixelDeck `1.27.078`.
- Pixel64's managed software renderer remains the default.
- Native rendering remains an explicit developer opt-in.
- Super Mario 64 (USA) revision 0 remains the only certified gameplay route.

## Native memory evidence

ABI 2 exposes paraLLEl-RDP's actual 4 MiB hidden RDRAM alongside the existing
8 MiB canonical RDRAM readback. Upload and download are size checked, occur
only after the command processor is idle, and never expose a C++ exception
through the C boundary.

Managed replay now reports:

- full output RDRAM and hidden-coverage SHA-256 hashes;
- exact changed-byte counts for both memories;
- the framebuffer region selected by the last `SetColorImage` and scissor;
- the depth region selected by `SetMaskImage`; and
- before/after hashes and changed-byte counts for both image regions.

The replay CLI includes all of those values in repeatable diagnostic output.

## ROM-free native gate

The native workflow builds the same pinned paraLLEl-RDP revision on Windows
and Linux. Linux installs Mesa's CPU Vulkan implementation and replays a
small deterministic RDP scene containing:

- separate RGBA16 framebuffer and depth images;
- a 16-by-16 scissor;
- a one-cycle shade combiner;
- sample-quad antialiasing and depth updates;
- one 44-word shaded, textured, depth-writing triangle; and
- a full synchronization command.

Hidden RDRAM starts at zero for this test. The gate fails unless native replay
changes the framebuffer, depth buffer, and hidden coverage, and then produces
identical hashes on a second fresh-context replay. This proves rendering work
and coverage readback without committing a copyrighted cartridge fixture.

Windows CI compiles the same C++ sources, validates ABI 2 and every required
export, and runs the managed loader/fallback suite. A hosted Windows runner
without Vulkan remains a valid software-fallback environment.

## Current verification boundary

The managed encoder, loader, layout analysis, fallback, and product-version
tests pass locally. The complete release solution builds without warnings.
This workstation has no C++/CMake toolchain, so the new native runtime gate
must receive its first compiled execution in GitHub Actions after the changes
are pushed.

The existing local Super Mario 64 gate proves that a real triangle-producing
task lowers to a complete packet stream with no omitted primitives or
unsupported source commands. The next certification step is to replay that
owned local capture through an ABI-2 build and record selected framebuffer,
depth, and coverage assertions without committing game-derived RDRAM.

## Remaining before default native rendering

- Certify selected real Mario framebuffer and depth assertions.
- Verify hidden-coverage continuity across consecutive graphics tasks.
- Triage texture-perspective and combiner/blender mismatches.
- Lower HLE lines and remaining custom-microcode generators.
- Validate VI scanout and presentation timing.
- Run on representative Windows GPUs and Raspberry Pi Vulkan hardware.

Until those gates pass, `PIXELDECK_N64_VIDEO_BACKEND=parallel-rdp` remains a
developer validation switch.
