# PixelDeck v1.25.076

PixelDeck 1.25.076 introduces Pixel64 0.12.017's optional paraLLEl-RDP native
renderer boundary. This is an integration and validation milestone; Pixel64's
managed Fast3D renderer remains the live gameplay default.

## Pixel64 0.12.017

- Pins the MIT-licensed `parallel-rdp-standalone` source at exact revision
  `388d70f5835b352d841d9d9e5a08c5de01470f41`.
- Adds a small versioned C ABI for Vulkan device creation, aligned RDRAM
  transfer, native RDP packets, VI registers, synchronized scanout, diagnostics,
  and deterministic teardown.
- Converts explicitly between Pixel64's canonical big-endian RDRAM and the
  32-bit word-swapped host layout consumed by paraLLEl-RDP.
- Loads the native runtime dynamically. Missing libraries, unsupported Vulkan
  devices, and ABI mismatches fall back cleanly to the managed renderer.
- Allows complete `.p64rdp` traces to use the bridge with
  `--parallel-rdp`; incomplete HLE traces are rejected before native loading.
- Builds and tests the native bridge on Windows x64 and Linux x64 CI with
  compiler concurrency capped at two workers.
- Full runtime packages include a staged native bridge when one is available.
  Architecture-neutral component updates remain managed-only.

## Still open

Ordinary Fast3D/F3DEX2 gameplay uses HLE triangles that Pixel64 has not yet
lowered to native RDP edge packets. The Vulkan backend will not become the live
default until those packets, hidden coverage, dual-backend image assertions,
and per-title fallback have been validated.

Super Mario 64 (USA) revision 0 remains Pixel64's verified gameplay route.
Other Nintendo 64 titles remain experimental attempts.

