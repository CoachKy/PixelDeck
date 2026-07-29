# PixelDeck library images

PixelDeck shows a picture on each game's library card. This folder holds the
pictures you have chosen deliberately, beside the `Games` and `Saves` folders
and using the same console and nested game-folder layout:

- `Nintendo` for NES and Famicom Disk System games.
- `SuperNintendo` for Super Nintendo games.
- `Nintendo64` for Nintendo 64 games.

For example, `Games/SuperNintendo/RPG/FF3.sfc` uses
`Library/SuperNintendo/RPG/FF3.png`.

There are three ways a game gets its picture, in order of priority:

1. An image placed next to the ROM itself, sharing its name
   (`Games/SuperNintendo/FF3.png`).
2. An image in this folder — either captured in game through **Take library
   image** in the pause menu, or simply dropped in by hand as a PNG named after
   the ROM.
3. Failing both, PixelDeck takes an automatic screenshot shortly after the game
   boots and caches it out of sight in `Games/.pixeldeck/screenshots`.

Once a game has a picture from options 1 or 2, the automatic screenshot leaves
it alone.

Library images are ignored by Git. Keep this folder in your normal backup
routine.
