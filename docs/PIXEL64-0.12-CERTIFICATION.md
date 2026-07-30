# Pixel64 0.12 certification

Pixel64 0.12.017 is the native-renderer integration milestone. It does not
declare paraLLEl-RDP the live default and does not expand the list of certified
playable games. It establishes the reproducible and fail-safe boundary needed
to compare Pixel64's output with a mature conformant RDP implementation.

- Product versions report Pixel64 `0.12.017` and PixelDeck `1.25.076`.
- Pixel64's existing managed Fast3D renderer remains the live default.
- A missing or incompatible native library, Vulkan loader, GPU, or ABI cannot
  prevent PixelDeck from starting or playing through the software fallback.
- Super Mario 64 (USA) revision 0 remains the verified gameplay route.
- Dashboard visuals and library navigation are unchanged.

## Pinned native dependency

The bridge consumes
[`parallel-rdp-standalone`](https://github.com/Themaister/parallel-rdp-standalone)
at exact Git revision
`388d70f5835b352d841d9d9e5a08c5de01470f41`. Its `COMMIT` file identifies
paraLLEl-RDP core revision
`1cecd042b2619bc505c12bfdc713808386f2b54d`.

The standalone tree supplies paraLLEl-RDP, the required Granite Vulkan subset,
embedded shaders, Volk, and Vulkan headers under the upstream MIT license.
PixelDeck's bridge is a small C ABI built as
`PixelDeck.ParallelRdp.dll` on Windows or
`libPixelDeck.ParallelRdp.so` on Linux.

## Implemented boundary

ABI version 1 provides:

- headless Vulkan context and device creation;
- a private 64 KiB-aligned 8 MiB RDRAM mirror;
- canonical big-endian RDRAM upload and download with explicit conversion to
  paraLLEl-RDP's little-endian 32-bit word-swapped host representation;
- variable-length native RDP command submission;
- all fourteen VI register values;
- synchronized RGBA8 scanout;
- native exception containment and thread-local diagnostic text; and
- deterministic teardown without allowing C++ exceptions across the ABI.

The managed loader locates a runtime-specific bridge dynamically, verifies ABI
version 1 before creating a context, and owns the native library until the
context is destroyed. `PIXELDECK_PARALLEL_RDP_LIBRARY` can select an explicit
developer build. Full packages include a tested runtime when it has been staged
under `artifacts/native/<rid>`; the platform-neutral component update does not
carry native binaries.

## Replay gate

`.p64rdp` traces marked complete can be replayed through the bridge:

```powershell
dotnet run --project tools/PixelDeck.N64GraphicsReplay -- `
  path/to/capture.p64rdp --repeat 3 --parallel-rdp
```

The native route refuses a trace with omitted HLE primitives or unsupported
source commands before it loads the bridge. This prevents a partial command
stream from being mistaken for a rendering improvement.

## Native build gate

Windows x64 and Linux x64 compile the exact pinned source, run an ABI smoke
test, install the native runtime plus upstream license, and publish the staged
runtime as a CI artifact. Native compilation is deliberately limited to two
workers because the embedded shader translation unit is memory intensive.

The equivalent local command is:

```powershell
./scripts/Build-ParallelRdp.ps1
```

This script only writes build products beneath `artifacts`; it does not install
a driver, Visual Studio workload, service, or system package.

## Remaining blocker for live gameplay

Pixel64 currently decodes Fast3D/F3DEX2 in HLE and rasterizes transformed
triangles directly. paraLLEl-RDP consumes the native edge, shade, texture, and
depth coefficient packets normally emitted by the RSP microcode. Pixel64 must
lower its clipped HLE triangles into those exact packets—or add a low-level RSP
backend—before the Vulkan renderer can receive complete ordinary gameplay
frames.

After that lowering is implemented, the next gate is dual-backend replay with
image assertions, hidden-coverage validation, and per-title fallback. Only then
should paraLLEl-RDP become selectable for live gameplay.

## Reproducing managed gates

```powershell
dotnet test tests/PixelDeck.App.Tests/PixelDeck.App.Tests.csproj `
  --filter FullyQualifiedName~ParallelRdpSupportTests

dotnet test tests/PixelDeck.App.Tests/PixelDeck.App.Tests.csproj `
  --filter FullyQualifiedName~ProductVersionTests

dotnet build PixelDeck.sln --no-restore
```

