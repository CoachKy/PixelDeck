# Pixel64 0.1 certification

Pixel64 0.1.001 is a development foundation, not a gameplay release.

## First cartridge target

- Super Mario 64 (USA), revision 0
- Internal game code: `NSME`
- Cartridge size: 8 MiB
- Header CRC pair: `635A2BFF / 8B022326`
- CIC: 6102
- Video region: NTSC

The local image is test input and is never included in the repository,
published packages, logs, or generated evidence.

## Passing gates

- Standard big-endian `.z64`, byte-swapped `.v64`, and little-endian `.n64`
  images normalize to one immutable big-endian cartridge representation.
- The cartridge header supplies the internal title, entry point, game code,
  country, revision, CRC pair, and CIC boot-code identity.
- The real local IPL3 completes 5,745,290 R4300i instructions and reaches
  `0x80246000`, the cartridge's exact entry point, without an unsupported CPU
  opcode.
- A 120-field OS/VI gate executes 93,750,000 instructions, services masked VI
  interrupts, and does not encounter an unsupported R4300i opcode.
- PI DMA copies cartridge bytes into RDRAM with big-endian bus order.
- SP DMA, status, semaphore, task discovery, and interrupt-completion
  foundations are present. A regression verifies graphics-task scheduling
  without pretending that the RSP or RDP has executed the task.
- SI/PIF polling returns port-specific buttons and signed analog-stick values.
- The shared SDL3 and XInput paths preserve raw left/right stick axes; Pixel64
  scales the left stick to the N64 range and maps the right stick to C-buttons.
- Four core controller slots exist. PixelDeck assigns its existing Player 1
  and Player 2 physical slots to ports 1 and 2.
- 512-byte EEPROM and interrupted-write recovery are wired to the game's
  durable PixelDeck save path.
- Save states validate the cartridge SHA-256, payload size, and payload
  integrity before restoring CPU, memory, video, controller, PIF, and EEPROM
  state.
- The Nintendo 64 library uses the same reusable gallery, alphabetical
  sections, index navigation, title count, play history, selected-game card,
  and version footer as Nintendo and Super Nintendo.
- Launching the target keeps an explicit development overlay visible until the
  video interface receives a useful rendered image, instead of presenting a
  blank screen as completed gameplay.

## Explicitly outside this milestone

- RSP scalar/vector microcode execution
- Fast3D display-list processing
- RDP triangle, texture, depth, blend, and coverage behavior
- AI DMA and N64 audio microcode
- Complete TLB and cache behavior
- Cycle-exact R4300i exceptions, branch-delay exception metadata, and memory
  latency
- Controller Pak, Rumble Pak, Transfer Pak, and four-player dashboard mapping
- A complete Super Mario 64 boot logo, menu, save-file, or gameplay route

Until those paths exist, Pixel64 remains below 1.0 and the dashboard labels the
target `PARTIAL`.
