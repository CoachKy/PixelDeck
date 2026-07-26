# PixelDeck games folder

PixelDeck keeps each console library in its own directory:

- `Nintendo` contains NES homebrew and locally owned `.nes` images.
- `SuperNintendo` contains Super Nintendo `.sfc` and `.smc` images. Supported standard cartridges appear in the same dashboard gallery and launch through PixelDeck's local SNES core.
- `Nintendo64` contains Nintendo 64 `.z64`, `.v64`, and `.n64` images. Pixel64 currently targets Super Mario 64 (USA) revision 0 as its first development cartridge.

PixelDeck creates all three directories automatically and scans their subdirectories.

Supported discovery formats currently include `.nes`, `.fds`, `.sfc`, `.smc`, `.gb`, `.gbc`, `.gba`, `.n64`, `.z64`, `.v64`, `.nds`, `.gcm`, `.rvz`, `.wbfs`, `.iso`, `.dol`, and `.elf`.

PixelDeck reads embedded SNES titles. A validated legacy Nintendo cartridge-header title can rescue an opaque 8.3-style NES filename, but it does not replace a readable filename because those 16-character fields are frequently abbreviated or inaccurate. For complete, filename-independent naming, place a standard ClrMamePro `.dat`, Logiqx XML catalog, or PixelDeck JSON catalog in `.pixeldeck/metadata`. Matching and caching stay entirely local; game files are never uploaded or renamed.

For a local dashboard image, place a screenshot beside the game using the same base filename, such as `Nintendo/My Game.nes` and `Nintendo/My Game.png`. PNG, JPEG, WebP, and BMP images are supported.

Game content is ignored by Git. The README files retain and document the system directories without publishing game images.
