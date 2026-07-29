# PixelDeck

PixelDeck is a local, controller-first game dashboard with in-repository NES, SNES, and early Nintendo 64 emulators. It scans the repository's `Games` folder and presents the files it discovers in a living-room interface.

## Run

```powershell
dotnet run --project src/PixelDeck.App
```

For direct emulator debugging, launch a discovered NES, SNES, or supported Nintendo 64 file without the dashboard:

```powershell
dotnet run --project src/PixelDeck.App -- --game "Games/Nintendo/My Game.nes"
```

Place NES homebrew under `Games/Nintendo`, Super Nintendo homebrew under `Games/SuperNintendo`, and Nintendo 64 homebrew under `Games/Nintendo64`. The system folders are created automatically, and the dashboard refreshes when files change.

To use local artwork, place a `.png`, `.jpg`, `.jpeg`, `.webp`, or `.bmp` beside the game with the same base filename. For example, `My Game.nes` will use `My Game.png`. PixelDeck also captures an in-game frame under `Games/.pixeldeck/screenshots` automatically.

Game titles are resolved locally. PixelDeck prefers an exact SHA-1/CRC match from an offline catalog in `Games/.pixeldeck/metadata`, then a cartridge's embedded title, and uses the filename only when neither source can identify the image. Standard ClrMamePro `.dat` and Logiqx XML catalogs are supported, as is PixelDeck's small JSON catalog format. NES matching checks both the complete iNES file and its headerless PRG/CHR payload, while SNES matching checks both copier-headered and headerless forms. Resolved results are cached in `Games/.pixeldeck/title-cache.json`, so unchanged games are not rehashed on every refresh. PixelDeck never contacts a naming server. See [ROM title metadata](docs/ROM-TITLE-METADATA.md) for details.

### Windows tester ZIP

Create a self-contained Windows x64 package from the repository root:

```powershell
./scripts/Publish-PixelDeckTester.ps1
```

The versioned ZIP, its expanded staging directory, and a SHA-256 checksum are
written beneath `artifacts/releases`. The package includes empty Nintendo,
Super Nintendo, and Nintendo 64 game folders and the .NET runtime, but never includes ROMs,
local saves, screenshots, metadata caches, or debug symbols. Friends can
extract the complete ZIP and run `PixelDeck.App.exe` without installing .NET.
See the included `README.md` for the tester checklist.

### Raspberry Pi ARM64

Create a framework-dependent Raspberry Pi build from the repository root:

```powershell
dotnet publish src/PixelDeck.App/PixelDeck.App.csproj -c Release -r linux-arm64 --self-contained false -o publish/linux-arm64
```

Copy the published directory and the `Games` directory to a 64-bit Raspberry Pi desktop with the .NET 10 runtime, then launch `PixelDeck.App` from that directory. SDL3 and its ARM64 native library are included in the publish. DualSense and Xbox-compatible controllers can use USB or an operating-system Bluetooth pairing. If Linux sees a controller but PixelDeck does not, give the desktop user read access to the distribution's input devices through its normal udev/input-group configuration, reconnect the controller, and restart PixelDeck. The ARM64 publish is build-validated; final display, audio, Bluetooth, thermal, and frame-pacing certification still requires a run on the target Pi.

## Controls

| Action | Keyboard | Controller |
| --- | --- | --- |
| Browse games | Arrow keys | D-pad / left stick |
| Open alphabetical index | Left from a gallery row's first game | D-pad left from a gallery row's first game |
| Jump from index to games | Right / Enter | D-pad right / A |
| Focus console switcher | Escape | B |
| Launch selected game | Enter | A |
| Refresh library | F5 | X |
| Jump to Home / Library / Settings / Quit | F1 / F2 / F3 / F4 | - |
| Quick-switch dashboard tabs | - | Left / right bumper |
| Move between content, console tabs, and dashboard tabs | Up / Down | D-pad / left stick |
| Quit from the Quit page | Enter | A |

The dashboard always runs fullscreen; use its Quit page to close PixelDeck. Home shows up to five genuinely played games, including total active play time, session count, and last-played time. Play history stays local in `%LOCALAPPDATA%\PixelDeck\play-history.json`; no sample activity is generated.

The Library uses one reusable six-column gallery for every console shelf. A compact `#`/`A`-`Z` index sits to the left of the games and the selected-game panel is narrowed on the right. Move left from the first card in a gallery row to enter the index, move vertically to choose a populated section, then press right or A to jump to its first title. B moves directly to the Nintendo/Super Nintendo switcher, where A returns to the gallery. The component counts titles, sorts them alphabetically inside each section, adds rows, and scrolls vertically as the collection grows. Library cards show each game's accumulated active play time. The heading totals play time for the currently selected console shelf, and the selected-game panel includes both total and last-played time.

Quitting opens a confirmation dialog with Cancel selected by default. Use the directional controls to choose, A or Enter to confirm the focused choice, and B or Escape to cancel. The window close button uses the same confirmation.

The Settings page uses a scalable controller paper doll. Select Player 1 or Player 2, assign one of four stable runtime controller slots, then select Nintendo or Super Nintendo to edit that player's console-specific mapping. The diagram updates immediately to show which game action is attached to each physical position. Nintendo profiles map A, B, Start, and Select; Super Nintendo profiles independently map A, B, X, Y, L, R, Start, and Select. The complete setup screen can be operated with a controller: Up/Down chooses a setting, Left/Right changes it, A advances it, and B returns to the dashboard tabs.

PixelDeck uses SDL's standardized gamepad layer on Windows and Linux, including Raspberry Pi ARM64, so Xbox-compatible and PlayStation DualSense controllers can be mixed. Face-button labels show both layouts, such as South (A / Cross), and Settings displays each detected controller's name and the active input backend. XInput remains a Windows fallback if SDL cannot initialize. The dashboard header continuously shows how many controllers are connected, while Settings reports the readiness of the assigned P1 and P2 slots. Existing slots stay stable while PixelDeck is running, including through ordinary hot-plug changes.

Both local controller ports are active during NES, SNES, and Nintendo 64 gameplay; Player 1 controls the dashboard, while either controller can open and operate the in-game pause menu. Nintendo 64 uses the same P1/P2 device assignments and preserves the physical analog stick instead of reducing it to a digital direction; the right stick supplies the four C buttons. Right Trigger / R2 is reserved for 2X play speed on NES and SNES: hold it on either controller and release it to return immediately to normal speed. NES and SNES now share the same 60.0988 Hz absolute host clock and bounded audio-queue feedback at normal speed; fast-forward targets exactly 120.1976 emulated frames per second. NES mono and SNES stereo audio remain active during fast-forward, and rate changes preserve queued samples instead of inserting a silence discontinuity. PixelDeck consumes two emulated audio frames per host frame so music and effects run at twice the speed and pitch without accumulating a delayed queue. Nintendo removes the original eight-sprites-per-scanline limit by default, preventing composite characters from flickering in crowded scenes such as Zelda II towns. The `Remove 8-sprite limit` setting can be disabled when hardware-accurate flicker is preferred. NES accuracy controls select the common RP2C02G or an early RP2C02B-or-older PPU, opt into deterministic electrical OAM decay, and choose a stable or collision-prone CPU/PPU OAM phase. Settings are stored locally in `%LOCALAPPDATA%\PixelDeck\settings.json`.

Inside the emulator, the controller's system button (Xbox Guide or PlayStation PS) opens the pause menu. Escape and Select + Start (View + Menu or Create + Options) are keyboard/controller fallbacks. The menu can resume, save state, load state, reset the cartridge, or quit to the dashboard. Save opens a per-game slot list with a new-slot choice and overwrite confirmation for existing slots. Load lists the game's existing slots and remains disabled when none exist. Legacy single-state files are preserved as numbered slots. Save states are cartridge-validated and stored under the sibling `Saves` folder in `Nintendo`, `SuperNintendo`, or `Nintendo64`, preserving any nested game-folder layout.

## NES core status

The in-repository core implements all 256 2A03 CPU opcode encodings, including the stable unofficial instructions and JAM behavior, controller ports, parity-correct OAM DMA, observable indexed dummy reads, the NMOS read/write/write sequence for memory-modifying instructions, soft-reset behavior, the main PPU registers and renderer, the five NES APU audio channels, save states, and cartridge mappers 0 (NROM), 1 (MMC1), 2 (UxROM), 3 (CNROM), 4 (MMC3), 5 (MMC5), 7 (AxROM), 8 (Super Magic Card mode 4), 9 (MMC2), 10 (MMC4), 11 (Color Dreams), 13 (CPROM), 15 (K-1029/K-1030P multicart), 19 (Namco 129/163), 21/22/23 (Konami VRC2/VRC4), 32 (Irem G-101), 33 (Taito TC0190), 34 (BNROM/NINA), 41 (Caltron), 64 (Tengen RAMBO-1), 66 (GxROM), 69 (Sunsoft FME-7/5B), 71 (Camerica), 75 (VRC1), 79 (NINA-03/06), 90 (J.Y. Company ASIC), 113, 118 (TxSROM), 119 (TQROM), 228, 232 (Camerica Quattro), and 240 (expansion-space GNROM). MMC5 includes its four PRG and CHR banking modes, protected work RAM, independent background/sprite CHR selection, CIRAM/ExRAM/fill nametable sources, extended attributes, scanline and PCM IRQs, multiplier, two pulse channels, 8-bit PCM, and mapper state restoration. MMC5 vertical split rendering remains outside the current compatibility envelope. VRC2/VRC4 support includes their board-specific address-line permutations, PRG/CHR banking, mirroring, VRC2 latch, VRC4 CPU/scanline IRQs, and mapper state restoration. RAMBO-1 includes its extended PRG/CHR modes and both filtered-A12 and CPU-cycle IRQ modes. A shared CPU-cycle scheduler advances the APU and cartridge timers once and the PPU three times for every CPU bus read, write, or idle cycle. It keeps distinct NMI, APU IRQ, and cartridge IRQ phases for instruction-boundary polling and implements NMI hijacking of BRK/IRQ entry. OAM and DMC DMA arbitrate the same get/put bus phases, including overlapping transfers, continuous controller-port reads, and simultaneous internal APU/controller plus external cartridge reads when the 2A03 bus collision selects both. The CPU data bus preserves the undriven controller pins, unmapped internal-I/O reads, and APU-status bit 5. The PPU produces each visible pixel on its individual dot from background pattern/attribute shift registers and active sprite counters/shifters. Background fetches, scrolling increments and copies, next-line sprite evaluation, and sprite pattern fetches run in their hardware rendering windows. MMC3 and RAMBO-1 see the resulting fetch addresses on every PPU dot instead of a synthetic scanline signal. Both Sharp/new and NEC/old zero-latch IRQ behaviors are implemented; Auto mode selects NES 2.0 mapper 4 submapper 0 or 4 metadata, and Dashboard Settings provides an override for ambiguous legacy iNES images. The dashboard also inspects RAM sizes, trainer, timing region, and default input device, sanitizes undefined fields in archaic iNES headers, and disables Play with an explicit compatibility status when the cartridge variant is unsupported.

Battery-backed cartridge RAM is persisted independently from save states under the sibling `Saves` folder. NES and SNES battery files use `.sav`; N64 files use `.eep`, `.sra`, or `.fla` according to the cartridge's storage type. Existing hashed files beneath `Games/.pixeldeck` are migrated without overwriting files already present in the new location. Audio is mixed to a 48 kHz mono stream and played through the default Windows output device. Pulse sweep/envelope, triangle, noise, CPU-arbitrated DMC sample fetching, frame IRQs, and DMC IRQs are implemented and included in save states. Initial DMC fetches observe the hardware two-or-three-cycle phase delay instead of beginning immediately. The mixer uses a continuous soft-knee output limiter instead of hard clipping.

The automated NES accuracy baseline passes Blargg's complete official/unofficial instruction suite, all eight primary APU tests, and all ten PPU vblank/NMI tests (20 baseline ROMs). Expanded validation also passes the official and unofficial instruction-timing ROMs, the four instruction-misc/dummy-read ROMs, both CPU dummy-write ROMs, both CPU reset ROMs, all five `cpu_interrupts_v2` ROMs, all six APU power/reset ROMs, PPU open-bus decay, the extended PPU read-buffer/DMA test, OAM read and randomized OAM stress, and all six MMC3 IRQ ROMs across their appropriate Sharp/new and NEC/old modes, including exact scanline-phase timing. The five visual sprite-overflow ROMs pass their basics, details, exact timing, diagonal-bug, and live-emulation checks; all eleven sprite-zero-hit ROMs also pass, including alignment, clipping, 8x16 sprites, and edge timing.

The render producer is decoupled from UI painting: when the window is busy, presentation skips stale frames and displays the newest completed frame without stalling CPU/APU emulation. The steady-state presentation path reuses its pixel buffer. A synthetic worst-case regression runs with rendering enabled, all 64 sprite slots active, and the sprite-limit enhancement enabled; it requires 300 frames to finish in less than half their real-time duration, keeps the 99th-percentile core frame below one NTSC frame, allocates no memory in the measured frame loop, and drops no core audio samples.

This is a strong compatibility and performance baseline, not a claim of perfect hardware emulation. Gameplay remains NTSC-only. Modern 2C02 secondary-OAM clearing and odd-read/even-write sprite evaluation are dot-scheduled, including exact overflow-dot timing and the diagonal n/m-counter bug that can compare tile, attribute, or X bytes as Y. Rendering-time `$2004` reads expose the active OAM latch, writes advance only the sprite-index bits, and the modern pre-render OAMADDR transition performs its row copy. The selectable early PPU path continues evaluation after primary OAM wraps and reproduces the partially populated X=$FF sprite artifact while omitting the later revision's pre-render row-copy bug.

Primary OAM now has independent refresh timestamps for all 32 electrical rows. When the optional decay model is enabled, forced blank refreshes only the OAMADDR-selected row while CPU, evaluation, clear, and fetch accesses refresh their complete physical rows; an untouched row settles after the measured 3,000-CPU-cycle window. Decayed values use a deterministic address-derived state because real post-decay bits and partial sub-dot smearing vary with the individual chip, voltage, temperature, and CPU/PPU alignment. Stable phase remains the default. The optional worst-case phase models unsynchronized `$2003` early-write transitions through the CPU open-bus row and direction-correct OAM1/OAM2 full-row copies when rendering changes during an access half-dot. The sprite-limit option preserves the ordinary eight-sprite limit and hardware overflow signal while optionally drawing later sprites. Legacy iNES cannot encode the MMC3 IRQ chip behavior, so Auto uses the common Sharp/new behavior and the user can select NEC/old in Settings when a legacy dump requires it. MMC5, Namco 163, and Sunsoft 5B expansion audio are mixed through the shared filtered output path. Zapper and other special peripherals remain unimplemented. NES 2.0 multicarts that advertise a standard controller plus Zapper are shown as `PARTIAL` rather than rejected.

NES save-state format version 19 includes the scheduler's interrupt phase history, CPU interrupt poll state and CPU open-bus value, shared DMA state, the in-flight DMC startup and delayed-disable phases, delayed APU frame-counter writes, the complete background/sprite PPU pipeline, secondary OAM, in-flight n/m evaluation counters, all OAM row-refresh timestamps, selected PPU revision and OAM collision profile, the current PPU bus address, MMC3 A12 filter and selected IRQ revision, MMC5 pulse/PCM audio and PCM IRQ state, delayed PPU rendering-mask writes, and per-bit PPU open-bus state. The payload has a bounded length and SHA-256 integrity check, and loading is transactional so invalid data cannot leave a partially restored machine. Battery RAM and dashboard save-state files use durable temporary writes and recover complete files left by an interrupted final rename. Older development save states and states created under a different MMC3 IRQ, PPU revision, decay, or OAM collision configuration are intentionally rejected instead of being restored incorrectly.

PixelNES 1.15.022 is the current feature build. It retains the 1.14 CPU, PPU, scheduler, DMA, mapper, and launch envelope while completing MMC5 expansion audio: two pulse voices use the MMC5 timer, envelope, and 240 Hz length behavior; the full-width PCM DAC supports direct and program-read modes; zero PCM values drive the mapper IRQ protocol; and all audio state survives save/load. The cartridge signal keeps MMC5's inverted polarity and enters PixelNES's existing shared filtered mixer. PixelDeck's visuals and presentation path are unchanged. PixelDeck masks the horizontal 8-pixel overscan edges by default so game-generated scrolling artifacts such as Mega Man's title-screen strip are hidden without changing PPU timing or game logic. The full 256-pixel output can be restored in Nintendo settings. Iteration 20 keeps the timing-critical PixelNES assembly optimized when PixelDeck is launched from an IDE's Debug configuration while retaining PDB debug symbols. Iteration 21 retains the exact 60.0988 Hz absolute frame clock and adds bounded audio-buffer feedback to the host wait. Iteration 22 adds a collection-wide compatibility laboratory that audits every local image without stopping at the first failure and publishes JSON, CSV, Markdown, timing, audio, video, duplicate-image, mapper, and deterministic save-state evidence. The July 25 baseline completed 818 images across 30 mapper families with 730 bounded passes, 88 review warnings, no failures, no unsupported or invalid images, no dropped audio, and no state-replay mismatch. Run `./scripts/Test-PixelNesRelease.ps1` for the pinned release gates or `./scripts/Test-PixelNesCompatibility.ps1` for the local collection audit. The exact supported envelope and evidence are documented in [PixelNES 1.15 certification](docs/PIXELNES-1.15-CERTIFICATION.md) and the [compatibility laboratory guide](docs/PIXELNES-COMPATIBILITY-LAB.md).

## SNES core status

PixelSNES 1.15.020 is the first release build of the in-repository SNES core. Sustained gameplay validation across the local library now backs a 1.0 claim: the core plays effectively the whole standard cartridge envelope. NTSC LoROM, HiROM, and ExHiROM images, including FastROM header variants and standard ROM, RAM, battery-backed RAM, DSP-1, or Capcom CX4 cartridge types, are supported. Copier-headered and headerless standard images are both accepted.

The one significant exclusion is the **Super FX (GSU-1/GSU-2) coprocessor**, which is not implemented. Star Fox, Super Mario World 2: Yoshi's Island, Stunt Race FX, and Doom will not run correctly. Super FX is the primary candidate for the next PixelSNES feature.

Iteration 1.15.020 promotes A Link to the Past from a boot check to a flagship gameplay route. A clean cartridge instance creates a player in fresh SRAM, reaches Link's house, renders a 52-color playable scene, moves Link in response to controller input, produces audible multi-voice S-DSP output without dropping samples, writes a durable 8 KiB battery save, and restores the exact next video frame and audio samples from a gameplay state. The same pass corrects the PPU's vertical mosaic phase: changing `$2106` during a visible field now restarts mosaic grouping from the live V-counter rather than permanently aligning every effect to scanline zero. Save-state format 14 preserves that in-flight phase, while formats 10-13 migrate with a top-of-field default.

Development iteration 0.14.018 replaces the S-CPU core's shared three/four-cycle approximation with the W65C816S base cycle matrix, taken-branch, emulation page-cross, unaligned direct-page, and indexed-read penalties. Every functional CPU access now contributes its mapped 6-, 8-, or 12-master-clock duration; `$420D` switches eligible cartridge banks to FastROM speed; and general DMA contributes global, per-channel, and per-byte CPU stalls. Save-state format 13 preserves FastROM selection and the master-clock phase, while formats 10-12 migrate with slow-ROM defaults. The synthetic timing contracts, 65C816 conformance screens, one-frame local cartridge sweep, and the real Super Mario World progression/save-state route pass under the new scheduler.

Development iteration 0.13.016 corrects SNES OBJ color-math eligibility: main-screen sprites using palettes 0-3 now remain opaque while palettes 4-7 participate in the blend selected by CGADSUB. Super Mario World's overworld Mario therefore retains his red, blue, and skin palette instead of being additively washed into a ghost. The real-game progression gate now captures and verifies the opaque overworld sprite before entering Yoshi's Island 1.

Development iteration 0.13.017 replaces the shared approximate layer ordering with the hardware priority order for PPU Modes 1 through 7. Mode 1's high BG1/BG2 tiles and optional topmost BG3 plane, Modes 2-6 foreground/background slots, and Mode 7 EXTBG high/low pixels now interleave correctly with all four OBJ priorities. Focused renderer gates cover every affected mode, and the real Super Mario World route still reaches the overworld and playable Yoshi's Island 1 with movement and exact save-state restoration.

Development iteration 0.13.015 added Capcom CX4 cartridge detection, its mirrored 8 KiB interface RAM, ROM-to-CX4 DMA, documented math/transform/sprite command interface, and save-state restoration without requiring the proprietary CX4 firmware. Mega Man X2 and Mega Man X3 pass visible and audible 2,400-frame cartridge gates with controller input, exact state restoration, real command-driven scenes, no BRK/COP path, and no unsupported SPC700 opcode. In the recorded certification path, X2 executes CX4 sprite command `$00` 1,963 times; X3 executes it 714 times and the vector-length command `$22` 460 times. The same pass fixes the S-CPU NMI enable edge: restoring `$4200` during VBlank no longer recursively enters an interrupt after software has acknowledged `$4210`. The CX4 behavior was implemented from public hardware documentation after comparing Snes9x's cartridge dispatch and command coverage; PixelSNES remains the only runtime core and no Snes9x code, native dependency, or firmware is included. The prior ExHiROM, DSP-1, PPU, and S-CPU improvements remain active: ROM mirrors follow the physical cartridge address lines, Modes 2/4/6 apply offset-per-tile data, Mode 5 uses horizontal character pairs, HDMA scroll writes use shared hardware latches, and Modes 3/4/7 decode direct color with the hardware bit layout.

OAM uses word-addressed low-table commits plus mirrored high-table access; sprite priority rotation, rectangular object modes, the 32-object and 34-sliver scanline limits, and their STAT77 overflow flags are active. VRAM reads use the hardware prefetch latch and increment port selected by VMAIN. CGRAM, OPHCT/OPVCT, STAT77, and STAT78 now retain their documented PPU data-bus bits, and counter latching follows WRIO bit 7 and its falling edge. The S-CPU multiplication and division registers advance over eight and sixteen CPU cycles rather than completing immediately. Automatic controller reads expose the HVBJOY busy bit for their 4,224-master-clock polling window and publish the hardware bit layout only when that window completes. Save-state format 14 preserves these in-flight operations, PPU read latches, mosaic phase, CX4 state, FastROM selection, and master-clock phase; format-10 through format-13 development states migrate with safe defaults for newer hardware state.

The local Super Mario World image completes its menus, enters a playable stage, responds to movement, and reproduces the exact next frame after a gameplay-state restore. A Link to the Past now creates a clean player file, reaches controllable in-house gameplay with audible audio, moves Link, persists battery SRAM, and reproduces the exact next frame and audio after a gameplay-state restore. Super Mario Kart completes its 1,200-frame DSP/Mode-7 boot check. PixelSNES also passes all 23 pinned 65C816 hardware-reference result screens, while current F-Zero and Pilotwings images complete extended Mode-7 boot checks. The current cartridge audit classified 289 local `.sfc`/`.smc` images as 211 LoROM, 76 HiROM, and 2 ExHiROM; both real 6 MiB ExHiROM images complete 120-frame boot gates, while the local Mega Man X2/X3 CX4 images complete visible, audible, command-driven 2,400-frame gates without a BRK/COP path. Images in the current cartridge envelope run without an unhandled core failure, while unsupported enhancement hardware remains an explicit rejection. These are bounded regression gates, not proof that every scene in those games is correct. DSP-1 calculations still use a wider host representation and quantize their results at the cartridge interface; exact DSP-1B integer edge behavior, every CX4 effect, and completed gameplay certification remain open before PixelSNES reaches 1.0.

Jumbo/ExLoROM, special peripherals, DSP-2/3/4, Super FX, SA-1, S-DD1, and SPC7110 are still rejected with an explicit dashboard explanation. Files with no credible internal header and reset vectors are rejected as malformed cartridge images rather than launched with a guessed map. PAL cartridges run at their native 50 Hz/312-scanline timing rather than being rejected; the map-mode byte is also no longer treated as a second, redundant gate once a checksum has already validated the header at that location, which recovers otherwise-standard LoROM carts that shipped with a nonstandard byte there (e.g. Contra III, Super Adventure Island).

The core implements the complete 65C816 opcode set, S-CPU open-bus behavior needed by the certified instruction suite, WRAM/SRAM mapping, general DMA, direct and indirect scanline HDMA, H/V timer IRQs, NMI, and both standard controller ports. The scanline renderer covers background modes 0-7, sprites, mosaic, windows, main/sub screens, fixed color, add/subtract/half color math, and Mode 7 affine transforms. Modes 5 and 6 are presented on PixelDeck's 256-pixel surface rather than exposed as a separate 512-pixel high-resolution output.

The audio path runs the SPC700, IPL ROM, communication ports, all three APU timers, and all eight S-DSP voices at 32 kHz stereo. It includes BRR decoding, Gaussian interpolation, ADSR/GAIN envelopes, pitch modulation, noise, echo/FIR, bounded output, overrun accounting, and state restoration. Audio is played through the default Windows output device.

Battery SRAM uses a durable temporary-write/replace sequence with interrupted-write recovery. Save-state format 14 is cartridge-validated, length-bounded, SHA-256 checked, and transactionally loaded so a bad state cannot partially mutate the running machine.

The earlier SNES certification run booted all six standard LoROM/HiROM cartridge variants, soaked seven local games for 126,000 frames with continuous audio and exact mid-run state restoration, and published Linux x64 and Linux ARM64 builds. PixelSNES 1.15.020 retains the focused PPU/OAM/controller/readback/fine-scroll/Mode-7 regressions and local Mario World, Zelda, Mario Kart, Final Fantasy III, Chrono Trigger, Donkey Kong Country, Super Metroid, Mega Man X, F-Zero, Pilotwings, and ExHiROM smoke coverage, plus synthetic CX4 memory/command/DMA/state contracts, real X2/X3 boot checks, the Super Mario World overworld OBJ-palette gate, exact background/OBJ priority regressions for Modes 1-7, access-speed-aware S-CPU scheduling, live-V-counter mosaic phase, and the clean-SRAM A Link to the Past gameplay gate. Sustained gameplay testing across the local library now supports the 1.0 release claim. This remains a compatibility and playability claim, not a claim of cycle-perfect S-CPU/PPU, DSP-1B, or CX4 timing. The Super FX coprocessor, WRAM refresh pauses, exact DMA alignment and post-write activation, cycle-stealing HDMA, dummy-access address speeds, mid-instruction event ordering, native 512-pixel high-resolution output, overscan/interlace, PAL timing, other enhancement chips, exact mid-scanline register timing, and on-device Raspberry Pi validation remain future work.

Run `./scripts/Test-PixelSnesRelease.ps1` from the repository root. The release claim and its Super FX exclusion are recorded in [PixelSNES 1.15 certification](docs/PIXELSNES-1.15-CERTIFICATION.md), the Zelda flagship evidence in [PixelSNES 0.15 certification](docs/PIXELSNES-0.15-CERTIFICATION.md), and the earlier withdrawn release envelope in the [historical PixelSNES 1.2 certification record](docs/PIXELSNES-1.2-CERTIFICATION.md).

SNES keyboard additions are A/S for X/Y and Q/W for L/R. All eight SNES buttons have their own controller mapping in Settings.

## Nintendo 64 core status

Pixel64 0.9.014 opens the cartridge-attempt envelope of PixelDeck's
in-repository Nintendo 64 core. Every structurally valid big-endian `.z64`,
byte-swapped `.v64`, and little-endian `.n64` image discovered in
`Games/Nintendo64` can now be launched without modifying the source file.
Super Mario 64 (USA) revision 0 (`NSME`, CIC-6102) remains the verified route;
launching another title is an attempt, not a compatibility claim.

The core reaches the real cartridge scheduler, handles the target's paired
64 KiB TLB mapping and FR=0 floating-point register mode, services SP/DP and AI
tasks, walks segmented Fast3D display lists, transforms and rasterizes
triangles, copies texture loads into persistent 4 KiB TMEM with row and
word-half swizzling, clips polygons in homogeneous coordinates, and honors the
RDP render mode when comparing or updating depth. Its software renderer applies
the programmed color combiner to textured and untextured spans, tracks the RDP
blend/fog colors, performs alpha comparison, and reads the framebuffer for
programmed blender cycles. Fast3D clip-space Y maps into the framebuffer's
top-left coordinate system with matching front/back winding, so the title and
castle grounds render upright. Timed SI DMA re-runs the resident PIF controller
command on every read. A state-driven local trace presses Start after the title
becomes interactive, selects Mario A, clears the opening dialog, reaches castle
area 1, leaves the intro action, and proves that holding the analog stick moves
Mario for 120 additional fields without an unknown CPU or Fast3D opcode.

Pixel64 is not yet graphics-accurate or generally game-compatible. An
unverified game may stop on CPU, RSP, RDP, or platform behavior the core does
not implement yet; PixelDeck reports that error in the emulator overlay instead
of rejecting the cartridge from the library. The upright
castle grounds execute and accept controller input, but exact RDP coverage,
dithering, two-cycle blending, lighting, and VI presentation remain incomplete;
additional visual errors are expected. The library therefore continues to mark
the target `PARTIAL`. The machine now submits graphics work through an
`IN64GraphicsBackend` boundary, allowing a conformant renderer to be added
without replacing the scheduler. Audio RSP tasks have a corresponding
`IN64AudioBackend` boundary. The bundled audio HLE preserves the ABI
resampler's four-sample cursor across task boundaries, while host playback
fully re-buffers after an underrun instead of repeatedly restarting from
partial fragments. A one-shot `.p64gfx` capture records the
graphics task plus its exact pre-execution RDRAM. That capture can now be
lowered into a versioned `.p64rdp` packet trace and replayed independently of
the Fast3D display-list decoder. Traces explicitly report any HLE triangles
that could not yet be represented as native RDP packets. The standalone replay
tool executes either input repeatedly without booting the game. PixelDeck's existing
gallery, alphabetical sections, title count, play history, version footer,
fullscreen pause menu, state slots, and assigned Player 1/Player 2 controller
ports are reused unchanged.

The shared SDL/XInput controller snapshot preserves raw left-stick magnitude
for the N64 analog stick and maps the right stick to the four C-buttons. A,
B, Start, Z, L, and R use the existing physical controller assignments and
shoulder/trigger layout. Pixel64's dedicated remapping page is not yet
available. Run the read-only
[Pixel64 compatibility laboratory](docs/PIXEL64-COMPATIBILITY-LAB.md) to audit
the local cartridge collection and produce per-title CPU, graphics, texture,
audio, performance, and save-state evidence. The earlier release envelope is
recorded in [Pixel64 0.4 certification](docs/PIXEL64-0.4-CERTIFICATION.md).
The component and conformance plan is tracked in the
[RMG-aligned Pixel64 roadmap](docs/PIXEL64-RMG-ROADMAP.md).

Set `PIXELDECK_GAMES_FOLDER` to override the default games directory.
