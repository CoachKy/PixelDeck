# PixelDeck games folder

PixelDeck keeps each console library in its own directory:

- `Nintendo` contains NES homebrew and locally owned `.nes` images.
- `SuperNintendo` contains Super Nintendo `.sfc` and `.smc` images. Supported standard cartridges appear in the same dashboard gallery and launch through PixelDeck's local SNES core.

PixelDeck creates both directories automatically and scans their subdirectories.

Supported discovery formats currently include `.nes`, `.fds`, `.sfc`, `.smc`, `.gb`, `.gbc`, `.gba`, `.n64`, `.z64`, `.v64`, `.nds`, `.gcm`, `.rvz`, `.wbfs`, `.iso`, `.dol`, and `.elf`.

For a local dashboard image, place a screenshot beside the game using the same base filename, such as `Nintendo/My Game.nes` and `Nintendo/My Game.png`. PNG, JPEG, WebP, and BMP images are supported.

Game content is ignored by Git. The README files retain and document the system directories without publishing game images.
