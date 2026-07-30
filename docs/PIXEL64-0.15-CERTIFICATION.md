# Pixel64 0.15 certification

Pixel64 0.15.020 is the multi-task native-state certification milestone.
PixelDeck reports version 1.28.079. Dashboard visuals and the default software
renderer remain unchanged.

## Sequence replay contract

`ParallelRdpTraceReplay.TryReplaySequence` executes ordered complete traces
through one paraLLEl-RDP context:

1. each task uploads its exact CPU-visible pre-task RDRAM;
2. the native RDP state and hidden coverage remain alive across tasks;
3. output RDRAM and hidden coverage are synchronized and hashed after each
   task; and
4. the next result's hidden-input hash must equal the preceding task's
   hidden-output hash.

This mirrors Pixel64's live backend more closely than independent one-task
replays. Any incomplete trace, native exception, or incompatible ABI fails
without changing the software-rendered gameplay default.

## ROM-free CI gate

Two non-overlapping deterministic triangles now run sequentially through one
native context. The second task must:

- receive the first task's exact hidden-coverage output;
- change its own framebuffer and depth image;
- change additional hidden-coverage bytes; and
- reproduce identical RDRAM and hidden hashes in a fresh sequence replay.

Linux CPU-Vulkan CI requires both the one-task and sequence gates. Windows
continues to compile and ABI-test the identical C++ bridge and verifies safe
fallback on hosted machines without Vulkan.

## Owned-local Mario gate

`scripts/Test-Pixel64ParallelRdp.ps1` locates the user's verified Super Mario
64 (USA) revision 0 image and invokes an opt-in certification test. The test:

- drives Pixel64 until it captures consecutive complete graphics tasks
  containing ordinary triangle packets and selected color/depth images;
- rejects frames where more than one graphics task would make the capture
  sequence ambiguous;
- replays the sequence twice through paraLLEl-RDP;
- requires framebuffer, depth, and hidden-coverage activity;
- verifies the hidden hash chain between tasks; and
- emits only compact hashes to the test log.

Cartridge bytes, pre-task RDRAM, screenshots, and native outputs remain local
and are never written into the repository.

The runner accepts `-NativeLibrary <path>` for a downloaded ABI-2 CI artifact.
On a machine with CMake and a C++ compiler it can instead use `-BuildNative`.

## Pre-refactor boundary

This gate is the contract the planned Pixel64 refactor must preserve:

- `N64Machine` owns scheduling and RSP task completion;
- graphics backends consume immutable task inputs;
- trace lowering is independent of native execution;
- native memory ownership is explicit;
- hidden coverage persists across tasks;
- software fallback remains available; and
- compatibility evidence stays deterministic and cartridge-safe.

The native CI artifact and owned-local Mario certification still have to be
executed after these changes are pushed. This workstation does not currently
have a C++/CMake toolchain.

## Remaining before refactoring

- Obtain a passing ABI-2 native CI artifact.
- Run the owned-local Mario sequence gate with that exact artifact.
- Record the pass and any framebuffer/depth/coverage mismatch categories.
- Keep the resulting hashes local; document only non-derived pass/fail
  conclusions.

Once those items pass, Pixel64 can be refactored around explicit CPU, RSP,
RDP, audio, scheduling, and host-presentation modules with these tests acting
as non-regression gates.
