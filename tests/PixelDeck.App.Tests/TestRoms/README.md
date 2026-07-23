# NES conformance ROMs

PixelDeck can run Blargg-compatible NES accuracy tests headlessly. Test binaries are
not committed because redistribution terms vary between suites.

Put `.nes` test files anywhere beneath this directory, or set
`PIXELDECK_NES_TEST_ROMS` to another directory. Then run:

```powershell
dotnet test tests/PixelDeck.App.Tests/PixelDeck.App.Tests.csproj -c Release --filter FullyQualifiedName~NesConformanceTests
```

The runner recognizes the standard `$6000-$6003` status protocol, waits up to
3,600 frames, and includes the test ROM's diagnostic text in failures.

PixelDeck's required baseline uses `instr_test-v5/official_only.nes`,
`instr_test-v5/all_instrs.nes`, all ten `ppu_vbl_nmi/rom_singles`, and all
eight `apu_test/rom_singles` from the NESdev emulator test catalog. All 20 ROMs
pass.

The following additional Blargg-protocol suites are useful for deeper local
validation and currently pass:

- `instr_timing` official and unofficial timing
- all four `instr_misc/rom_singles`
- both `cpu_dummy_writes` ROMs
- both `cpu_reset` ROMs
- all five `cpu_interrupts_v2` ROMs
- all six `apu_reset` ROMs
- `ppu_open_bus/ppu_open_bus.nes`
- `ppu_read_buffer/test_ppu_read_buffer.nes`
- `oam_read/oam_read.nes`
- all six MMC3 ROMs from `mmc3_test_2` across their appropriate IRQ modes,
  including exact scanline-phase timing

Repository unit coverage also verifies that OAM DMA is executed as 256
scheduled read/write pairs with the correct 513/514-cycle parity, that DMC and
OAM arbitrate overlapping get/put phases on the shared CPU bus, and that MMC3
does not count the false background A12 edge at visible-line dot 5. PPU
regressions verify that frame-buffer pixels appear only after their individual
dots execute, background fetches load the pattern and attribute shifters,
active sprite shifters remain independent from next-line evaluation, overflow
is raised on the exact even evaluation dot, and the diagonal overflow search
can interpret a later sprite's tile byte as Y. A mid-evaluation save-state
round trip preserves that in-flight n/m-counter state. Rendering-time OAM
regressions cover the `$2004` clear/evaluation/fetch latch, suppressed writes
and OAMADDR+4 behavior, the revision-specific pre-render row-copy bug,
early-PPU post-wrap X=$FF sprites, row-level electrical decay and refresh,
worst-case `$2003` open-bus row copies, and direction-correct rendering-toggle
collisions. Save-state coverage preserves OAM charge age and rejects a state
created under a different PPU accuracy profile.

The alternate `6-MMC3_alt.nes` ROM uses the NEC/old zero-latch behavior, while
ROM 5 uses the incompatible Sharp/new behavior. The test ROMs have legacy iNES
headers, so run ROM 6 with the explicit override:

```powershell
$env:PIXELDECK_NES_MMC3_IRQ_REVISION = 'Nec'
dotnet test tests/PixelDeck.App.Tests/PixelDeck.App.Tests.csproj -c Release --filter FullyQualifiedName~NesConformanceTests
```

Unset the variable or use `Auto`/`Sharp` for ROMs 1 through 5. Production Auto
mode reads NES 2.0 mapper 4 submapper 4 as NEC/old and submapper 0 as
Sharp/new; Dashboard Settings exposes the same override for ambiguous legacy
iNES cartridges.

Some catalog ROMs are visual or interactive and do not publish the
`$6000-$6003` protocol. A runner timeout on those ROMs is not a compatibility
result. `sprite_overflow_tests` instead report their result in `$00F8`, and
`sprite_hit_tests_2005.10.05` use the same convention; `$01` is pass. Set
`PIXELDECK_NES_VISUAL_RESULT_ADDRESS` to `00F8` to make the runner validate
that convention. For visual inspection, set
`PIXELDECK_CAPTURE_NES_CONFORMANCE` to a capture folder.
`PIXELDECK_NES_TEST_MAX_FRAMES` can shorten a visual-only run without changing
the normal 3,600-frame protocol timeout. Keep all test binaries outside Git or
place them in this ignored directory.
