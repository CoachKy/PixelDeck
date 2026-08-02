# PixelDeck saves folder

PixelDeck stores every ROM-created battery save and save state beside the
`Games` folder while preserving the console and nested game-folder layout:

- `Nintendo` contains NES and Famicom Disk System saves.
- `SuperNintendo` contains Super Nintendo saves.
- `Nintendo64` contains Nintendo 64 saves.
- `GameCube` will contain GameCube memory card saves as `.gci`. PixelCube
  cannot run a game yet, so nothing is written here today.

For example, `Games/SuperNintendo/RPG/FF3.sfc` uses
`Saves/SuperNintendo/RPG/FF3.sav` for cartridge SRAM and numbered files such
as `FF3.slot-001.state` for save states.

Nintendo 64 battery files use the conventional extension for the cartridge's
storage type: `.eep`, `.sra`, or `.fla`.

Save data is ignored by Git. Keep this folder in your normal backup routine.
