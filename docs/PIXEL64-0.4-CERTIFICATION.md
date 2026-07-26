# Pixel64 0.4 certification

Pixel64 0.4.006 expands the Nintendo 64 cartridge launch envelope. It is not a
graphics-accurate or generally compatible Nintendo 64 release.

## Cartridge attempt envelope

- Every structurally valid `.z64`, `.v64`, and `.n64` image discovered under
  `Games/Nintendo64` is offered as a launchable `PARTIAL` library entry.
- Big-endian, byte-swapped, and little-endian images are normalized in memory.
  The source cartridge file is never modified.
- CIC-6101, CIC-6102, CIC-6103, CIC-6105, and CIC-6106 boot codes select their
  matching startup seed.
- An unrecognized CIC is allowed to attempt boot with Pixel64's default startup
  seed and is clearly labeled `UNKNOWN CIC`.
- Malformed files and files without a recognized Nintendo 64 cartridge header
  remain `UNSUPPORTED`.
- An unsupported CPU, RSP, RDP, or platform operation is reported inside the
  emulator overlay so the user can open the pause menu and return safely.

## Verified route

Super Mario 64 (USA), revision 0 (`NSME`, CIC-6102) remains the only verified
gameplay route. The local trace reaches controllable castle gameplay and proves
that live analog input moves Mario. Its current graphics output remains partial.

Opening the launch envelope does not certify other cartridges. Each additional
title may stop immediately, boot partially, or expose missing hardware behavior.
Those results are now actionable compatibility evidence instead of being hidden
behind a dashboard launch restriction.

## Release gate

- Synthetic unverified cartridges must remain launchable and marked `PARTIAL`.
- The core load path must execute at least one instruction from an unverified,
  structurally valid cartridge rather than rejecting it by title.
- The Super Mario 64 verified-route regression must remain passing.
- Product versions must report Pixel64 `0.4.006` and PixelDeck `0.18.059`.

Pixel64 remains below 1.0 until a broader game matrix, graphics accuracy, RSP
microcode, audio synthesis, and platform timing are certified.
