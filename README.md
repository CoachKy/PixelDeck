# PixelDeck

PixelDeck is a local, controller-first game dashboard with in-repository NES and early SNES emulators. It scans the repository's `Games` folder and presents the files it discovers in a living-room interface.

## Run

```powershell
dotnet run --project src/PixelDeck.App
```

For direct emulator debugging, launch a discovered NES or SNES file without the dashboard:

```powershell
dotnet run --project src/PixelDeck.App -- --game "Games/Nintendo/My Game.nes"
```

Place NES homebrew under `Games/Nintendo` and Super Nintendo homebrew under `Games/SuperNintendo`. Both system folders are created automatically, and the dashboard refreshes when files change.

To use local artwork, place a `.png`, `.jpg`, `.jpeg`, `.webp`, or `.bmp` beside the game with the same base filename. For example, `My Game.nes` will use `My Game.png`. PixelDeck also captures an in-game frame under `Games/.pixeldeck/screenshots` automatically.

## Controls

| Action | Keyboard | XInput controller |
| --- | --- | --- |
| Browse games | Arrow keys | D-pad / left stick |
| Launch selected game | Enter | A |
| Refresh library | F5 | X |
| Open games folder | Click button | Y |
| Toggle fullscreen | F11 | Menu |
| Jump to Home / Library / Settings / Quit | F1 / F2 / F3 / F4 | - |
| Quick-switch dashboard tabs | - | Left / right bumper |
| Move between content, console tabs, and dashboard tabs | Up / Down | D-pad / left stick |
| Quit from the Quit page | Enter | A |

Home shows up to five genuinely played games, including total active play time, session count, and last-played time. Play history stays local in `%LOCALAPPDATA%\PixelDeck\play-history.json`; no sample activity is generated.

The Library is a six-column gallery that adds rows and scrolls vertically as the collection grows. Library cards show each game's accumulated active play time. The heading totals play time for the currently selected Nintendo or Super Nintendo shelf, and the selected-game panel includes both total and last-played time.

Quitting opens a confirmation dialog with Cancel selected by default. Use the directional controls to choose, A or Enter to confirm the focused choice, and B or Escape to cancel. The window close button uses the same confirmation.

The Settings page selects XInput controller 1-4 and provides separate mappings for Nintendo (A, B, Start, Select) and Super Nintendo (A, B, X, Y, L, R, Start, Select). Right Trigger is reserved in both systems: hold it for 2X play speed and release it to return immediately to normal speed. Nintendo also has an optional `Remove 8-sprite limit` enhancement. It renders sprites beyond the original console's per-scanline limit while leaving the hardware-accurate behavior as the default. Settings are stored locally in `%LOCALAPPDATA%\PixelDeck\settings.json`.

Inside the emulator, the Xbox/Guide button opens the pause menu. Escape and View + Menu are keyboard/controller fallbacks. The menu can resume, save state, load state, reset the cartridge, or quit to the dashboard. Save states are cartridge-validated and stored locally beneath `Games/.pixeldeck`.

## NES core status

The in-repository core implements all 256 2A03 CPU opcode encodings, including the stable unofficial instructions and JAM behavior, controller ports, parity-correct OAM DMA, observable indexed dummy reads, the NMOS read/write/write sequence for memory-modifying instructions, soft-reset behavior, the main PPU registers and renderer, the five NES APU audio channels, save states, and cartridge mappers 0 (NROM), 1 (MMC1), 2 (UxROM), 3 (CNROM), 4 (MMC3), 7 (AxROM), and 66 (GxROM). A shared CPU-cycle scheduler advances the APU once and the PPU three times for every CPU bus read, write, or idle cycle. It keeps distinct NMI, APU IRQ, and cartridge IRQ phases for instruction-boundary polling and implements NMI hijacking of BRK/IRQ entry. OAM and DMC DMA arbitrate the same get/put bus phases, including overlapping transfers and suppressed repeated controller reads. The PPU produces each visible pixel on its individual dot from background pattern/attribute shift registers and active sprite counters/shifters. Background fetches, scrolling increments and copies, next-line sprite evaluation, and sprite pattern fetches run in their hardware rendering windows. MMC3 sees the resulting fetch addresses on every PPU dot instead of a synthetic scanline signal. Both Sharp/new and NEC/old zero-latch IRQ behaviors are implemented; Auto mode selects NES 2.0 mapper 4 submapper 0 or 4 metadata, and Dashboard Settings provides an override for ambiguous legacy iNES images. The dashboard also inspects RAM sizes, trainer, timing region, and default input device, and disables Play with an explicit compatibility status when the cartridge variant is unsupported.

Battery-backed cartridge RAM is persisted independently from save states under `Games/.pixeldeck/saves`. Audio is mixed to a 48 kHz mono stream and played through the default Windows output device. Pulse sweep/envelope, triangle, noise, CPU-arbitrated DMC sample fetching, frame IRQs, and DMC IRQs are implemented and included in save states. The mixer uses a continuous soft-knee output limiter instead of hard clipping.

The automated NES accuracy baseline passes Blargg's complete official/unofficial instruction suite, all eight primary APU tests, and all ten PPU vblank/NMI tests (20 baseline ROMs). Expanded validation also passes the official and unofficial instruction-timing ROMs, the four instruction-misc/dummy-read ROMs, both CPU dummy-write ROMs, both CPU reset ROMs, all five `cpu_interrupts_v2` ROMs, all six APU power/reset ROMs, PPU open-bus decay, the extended PPU read-buffer/DMA test, OAM read and randomized OAM stress, and all six MMC3 IRQ ROMs across their appropriate Sharp/new and NEC/old modes, including exact scanline-phase timing. The five visual sprite-overflow ROMs pass their basics, details, exact timing, diagonal-bug, and live-emulation checks; all eleven sprite-zero-hit ROMs also pass, including alignment, clipping, 8x16 sprites, and edge timing.

The render producer is decoupled from UI painting: when the window is busy, presentation skips stale frames and displays the newest completed frame without stalling CPU/APU emulation. The steady-state presentation path reuses its pixel buffer. A synthetic worst-case regression runs with rendering enabled, all 64 sprite slots active, and the sprite-limit enhancement enabled; it requires 300 frames to finish in less than half their real-time duration, keeps the 99th-percentile core frame below one NTSC frame, allocates no memory in the measured frame loop, and drops no core audio samples.

This is a strong compatibility and performance baseline, not a claim of perfect hardware emulation. Gameplay remains NTSC-only. Modern 2C02 secondary-OAM clearing and odd-read/even-write sprite evaluation are dot-scheduled, including exact overflow-dot timing and the diagonal n/m-counter bug that can compare tile, attribute, or X bytes as Y. Rendering-time `$2004` reads expose the active OAM latch, writes advance only the sprite-index bits, nonzero pre-render OAMADDR performs its row copy, and rendering toggles reproduce clear/fetch-window OAM row corruption. OAM electrical decay, early-PPU post-wrap artifacts, and sub-dot analog behavior remain outside the model; the sprite-limit option preserves the ordinary eight-sprite limit and hardware overflow signal while optionally drawing later sprites. Legacy iNES cannot encode the MMC3 IRQ chip behavior, so Auto uses the common Sharp/new behavior and the user can select NEC/old in Settings when a legacy dump requires it. Zapper and other special peripherals and cartridge expansion audio are not implemented. NES 2.0 multicarts that advertise a standard controller plus Zapper are shown as `PARTIAL` rather than rejected.

NES save-state format version 14 includes the scheduler's interrupt phase history, CPU interrupt poll state, shared DMA state, delayed APU frame-counter writes, the complete background/sprite PPU pipeline, secondary OAM, in-flight n/m evaluation counters and pending OAM corruption rows, its current bus address, MMC3 A12 filter and selected IRQ revision, delayed PPU rendering-mask writes, and per-bit PPU open-bus state. Older development save states and states created under a different MMC3 IRQ revision are intentionally rejected instead of being restored incorrectly.

## SNES core status

The early in-repository SNES core parses copier-headered and headerless standard LoROM/HiROM cartridges, runs a 65C816 CPU, maps WRAM/SRAM, performs general DMA, handles H/V timer IRQs and controllers, renders an initial 256x224 Mode 0/1 background and sprite frame, and supports cartridge-validated save states. Its audio path runs the SPC700, IPL ROM, communication ports, and APU timers, then mixes all eight S-DSP voices to 32 kHz stereo with BRR decoding, Gaussian interpolation, ADSR/GAIN envelopes, pitch modulation, noise, and echo/FIR. Audio is played through the default Windows output device.

Current smoke coverage reaches visible boot scenes and non-silent game-driven audio in Chrono Trigger, Donkey Kong Country, Final Fantasy III, The Legend of Zelda: A Link to the Past, Mega Man X, and Super Mario World; Zelda also progresses through its intro to Player Select.

Supported SNES cartridges use the same `READY` library presentation as supported NES cartridges. This still represents boot-level compatibility, not a claim that those games are correct from beginning to end. Enhancement chips, HDMA, additional video modes and effects, and cycle-accurate CPU/PPU/APU timing remain to be implemented.

SNES keyboard additions are A/S for X/Y and Q/W for L/R. All eight SNES buttons have their own controller mapping in Settings.

Set `PIXELDECK_GAMES_FOLDER` to override the default games directory.
