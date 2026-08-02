# PixelDeck v1.29.086

Launcher 1.0.0 — unchanged, so this release can be applied as a component-only
update over any earlier launcher-1.0.0 install.

## Nintendo 64

- **Microcode detection no longer downgrades on an unreadable banner.** A task
  whose banner could not be read was classified as legacy Fast3D, so the rest of
  the cartridge's F3DEX2 display lists were decoded against the wrong opcode
  table. WWF WrestleMania 2000 flipped roughly 25 seconds in, logged 28,845
  unsupported commands, and stopped drawing entirely.
- **Controller pak transfers always answer.** A pak read or write whose lengths
  the port could not service returned without setting the receive descriptor,
  and games that block on the PIF — Super Mario 64 among them — hung on the next
  controller poll.
- **Rumble Pak support.** Occupied ports report an accessory, pak reads identify
  a Rumble Pak, and motor writes drive force feedback through SDL. Motors stop
  when a game unloads.
- **Video output size comes from the scan window.** Width was taken from
  VI_WIDTH, which is the frame-buffer stride rather than the visible image, so
  cartridges that stride wider than they display were sized wrong.
- **Colour combiner rewritten against the per-slot source tables.** The four mux
  inputs share selectors 0-5 but diverge above that, and one shared table was
  returning white where the hardware returns a key centre. Alpha C uses its own
  table, and the primitive LOD fraction from G_SETPRIMCOLOR is no longer
  discarded.
- COPY-mode texture rectangles bypass the combiner, as the hardware does.

## Diagnostics

- **NES and SNES now write to the trace log** at `%LOCALAPPDATA%\PixelDeck\emulator.log`,
  alongside the existing Nintendo 64 lines. NES reports mapper, PPU control and
  mask, and rendering state; SNES reports the enhancement chip, background mode,
  DMA and HDMA activity, and coprocessor progress. Both report a distinct-colour
  count, which is what separates a blank screen from a rendered one.
- Diagnostics are written on a background thread, so logging never blocks
  emulation, and the log trims its oldest half instead of emptying itself —
  keeping the cartridge and backend lines written at load.

## Installing on Linux (Raspberry Pi 5 and other arm64)

The archive is built on Windows, which has no Unix permission model, so nothing
inside it is marked executable. Set the bit after extracting:

```bash
tar -xzf PixelDeck-v1.29.086-linux-arm64.tar.gz
chmod +x PixelDeck
./PixelDeck
```

The build is self-contained, so no .NET runtime is required. A desktop session
is — PixelDeck draws through X11 or Wayland and will not start over a plain SSH
connection.

## Known issues

- Some cartridges show wedge-shaped geometry radiating from the centre of the
  frame. The renderer reports the affected vertices as `centrepinned` in the
  trace log; the cause is not yet identified.
- Nintendo 64 titles that lean on palette textures can show banded colour noise.
- Windows builds are unsigned, so SmartScreen will warn on first run.
