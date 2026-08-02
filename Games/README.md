# PixelDeck games folder

PixelDeck keeps each console library in its own directory:

- `Nintendo` contains NES homebrew and locally owned `.nes` images.
- `SuperNintendo` contains Super Nintendo `.sfc` and `.smc` images. Supported standard cartridges appear in the same dashboard gallery and launch through PixelDeck's local SNES core.
- `Nintendo64` contains Nintendo 64 `.z64`, `.v64`, and `.n64` images. Pixel64 currently targets Super Mario 64 (USA) revision 0 as its first development cartridge.
- `GameCube` contains GameCube discs as `.iso`, `.gcm`, `.ciso`, or `.rvz`. PixelCube reads a disc's header, file table, and boot executable, places the boot image in memory, and runs its Gekko CPU, but there is no graphics or audio hardware yet. A readable disc launches and produces a trace log rather than a game; see `docs/PIXELCUBE-TRACE-LOG.md`.

  RVZ support covers GameCube discs compressed with Zstandard, which is what Dolphin writes by default. Wii discs, and the older GCZ and WIA containers, are not readable.

PixelDeck creates all four directories automatically and scans their subdirectories.

`.iso` is the ordinary extension for a GameCube disc and for a disc image of
anything else, so PixelDeck treats one as GameCube only when it sits inside the
`GameCube` folder. An `.iso` elsewhere in the library is listed as a generic
disc image, as before.

ROM-created files are kept out of the game library. Battery saves and numbered
save states are written to the sibling `Saves` folder using the same console
and nested folder layout.

Supported discovery formats currently include `.nes`, `.fds`, `.sfc`, `.smc`, `.gb`, `.gbc`, `.gba`, `.n64`, `.z64`, `.v64`, `.nds`, `.gcm`, `.ciso`, `.rvz`, `.wbfs`, `.iso`, `.dol`, and `.elf`.

PixelDeck reads embedded SNES titles. A validated legacy Nintendo cartridge-header title can rescue an opaque 8.3-style NES filename, but it does not replace a readable filename because those 16-character fields are frequently abbreviated or inaccurate. For complete, filename-independent naming, place a standard ClrMamePro `.dat`, Logiqx XML catalog, or PixelDeck JSON catalog in `.pixeldeck/metadata`. Matching and caching stay entirely local; game files are never uploaded or renamed.

For a local dashboard image, place a screenshot beside the game using the same base filename, such as `Nintendo/My Game.nes` and `Nintendo/My Game.png`. PNG, JPEG, WebP, and BMP images are supported.

Game content is ignored by Git. The README files retain and document the system directories without publishing game images.
