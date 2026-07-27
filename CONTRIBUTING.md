# Contributing to PixelDeck

Thanks for your interest. PixelDeck is an Avalonia dashboard around three
from-scratch emulation cores: PixelNES, PixelSNES, and Pixel64.

## Ground rules

**No ROMs, ever.** Do not commit cartridge images, BIOS files, save data, or
screenshots taken from copyrighted games into this repository, and do not link
to them in issues. `Games/` ships empty on purpose. Tests that need a
commercial cartridge look for one locally and skip when it is absent.

**Emulation code must be clean-room.** Write cores from public hardware
documentation — the N64 programming manuals, the n64brew wiki, NESdev,
fullsnes, and similar references. **Do not copy or adapt code from other
emulators.** Many (mupen64plus, Dolphin, PCSX2) are GPL-licensed and copying
from them would force PixelDeck off its MIT license. If a reference was
essential to understanding something, cite it in a comment.

## Getting started

```bash
dotnet build
dotnet test tests/PixelDeck.App.Tests/PixelDeck.App.Tests.csproj
dotnet run --project src/PixelDeck.App
```

The full suite includes long certification soaks. For a fast inner loop, filter
to the area you are working on:

```bash
dotnet test --filter "FullyQualifiedName~N64"
```

## Standards

- `.editorconfig` defines the house style; your editor should pick it up.
- **The build is warning-clean.** Keep it that way rather than suppressing
  findings at the call site. If a rule genuinely does not fit the domain,
  disable it in `.editorconfig` with a comment explaining why.
- Comments should explain *why*, not restate *what*. The codebase has no
  `TODO`/`HACK` markers — please do not start.
- Add a test for behaviour you change. Emulator regressions are silent and
  expensive; a test is usually the only thing standing between a working core
  and a subtly broken one.

## Performance

The emulation cores are real-time software. `NesPerformanceTests` and
`N64PerformanceTests` assert allocation-free frame loops and throughput floors.
Wall-clock assertions are skipped on CI (shared runners are too noisy to gate
on), but allocation assertions always apply. Do not add work to per-pixel or
per-instruction paths without measuring first.

## Versioning

Each assembly versions independently as `major.minor.patch`, where **minor is
the feature count** and **patch is the iteration count** — this is not semver.
`ProductVersionTests` pins the numbers, so a bump means editing the `.csproj`
and that test together.

## Reporting compatibility

For a game that misbehaves, the per-cartridge diagnostic is the most useful
thing you can attach:

```bash
PIXEL64_TRACE_CART="Some Game" PIXEL64_TRACE_FIELDS=800 \
  dotnet test --filter "FullyQualifiedName~TraceLocalCartridge"
```

It reports instruction counts, microcode detection, RSP task counts, video
interface registers, and any unsupported opcodes.
