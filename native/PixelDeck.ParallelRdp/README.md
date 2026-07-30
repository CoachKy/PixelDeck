# PixelDeck paraLLEl-RDP bridge

This optional native library gives Pixel64 a narrow, versioned C ABI over
[paraLLEl-RDP standalone](https://github.com/Themaister/parallel-rdp-standalone).
It does not install a Vulkan driver, register a service, or modify the machine.
Pixel64 loads it dynamically and keeps the managed software renderer available
when the library or a compatible Vulkan device is absent.

The dependency is pinned to standalone revision
`388d70f5835b352d841d9d9e5a08c5de01470f41`, which contains paraLLEl-RDP core
revision `1cecd042b2619bc505c12bfdc713808386f2b54d`.

## Build

```powershell
cmake -S native/PixelDeck.ParallelRdp -B artifacts/validation/parallel-rdp -A x64
cmake --build artifacts/validation/parallel-rdp --config Release --parallel 2
ctest --test-dir artifacts/validation/parallel-rdp -C Release --output-on-failure
```

To build without another network fetch, point CMake at a matching standalone
checkout:

```powershell
cmake -S native/PixelDeck.ParallelRdp `
  -B artifacts/validation/parallel-rdp `
  -DPIXELDECK_PARALLEL_RDP_SOURCE_DIR=C:/path/to/parallel-rdp-standalone
```

The bridge accepts canonical big-endian N64 RDRAM bytes from the managed core
and converts them to paraLLEl-RDP's 32-bit word-swapped host layout. The reverse
conversion is applied on readback.

