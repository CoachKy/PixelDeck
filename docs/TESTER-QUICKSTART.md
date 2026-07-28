# PixelDeck tester quick start

This is a self-contained Windows test build. It includes the .NET runtime, so
you do not need to install the .NET SDK or runtime.

1. Extract the entire ZIP to a normal writable folder.
2. Put legally obtained homebrew or game images in:
   - `Games\Nintendo` for `.nes` images.
   - `Games\SuperNintendo` for `.sfc` or `.smc` images.
   - `Games\Nintendo64` for `.z64`, `.v64`, or `.n64` images.
3. Run `PixelDeck.App.exe`.

Do not run PixelDeck from inside the ZIP. Keep every published DLL and native
library beside the executable.

When updating to another test build, preserve the existing `Games` and `Saves`
folders. `Saves` contains local battery saves and save states grouped beneath
`Nintendo`, `SuperNintendo`, and `Nintendo64`. `Games\.pixeldeck` contains
local metadata and screenshots. Existing saves in the old cache location are
migrated automatically without overwriting saves already in `Saves`.
Either copy those folders into the new extracted build or replace the
application files without deleting them.

Windows may show a SmartScreen warning because this early test build is not
code-signed. Only continue if the ZIP came directly from the PixelDeck
developer and its SHA-256 checksum matches the separately supplied checksum.

When reporting a problem, include:

- The PixelDeck, PixelNES, or PixelSNES version shown in the dashboard.
- The game title and region/revision.
- What happened and what you expected.
- Whether the problem happens from a fresh boot or only after loading a state.
- A screenshot or short recording when the problem is visual.

Do not send ROM images with a bug report.
