# ROM title metadata

PixelDeck resolves a discovered game's display title in this order:

1. An exact SHA-1 or CRC-32 match in a local catalog.
2. A credible embedded cartridge title.
3. The file's base name.

This distinction matters for Nintendo Entertainment System images. The standard
16-byte iNES/NES 2.0 header describes cartridge hardware, not the game's name.
Some licensed cartridges contain a separate Nintendo header near the end of
PRG-ROM, but that header is absent from many games and its 16-character title
can be abbreviated or inaccurate. PixelDeck uses a checksum-valid title from
that header only to rescue an opaque 8.3-style filename; it does not let one
replace an already readable filename. A checksum catalog is therefore the
reliable way to assign complete NES titles independently of filenames.

SNES cartridges normally contain a 21-byte internal title. PixelDeck reads that
title from the selected LoROM or HiROM header even when no catalog is installed.

## Offline catalogs

Place catalog files directly in:

```text
Games/.pixeldeck/metadata
```

PixelDeck accepts:

- ClrMamePro-style `.dat` files with `game`, `name`/`description`, and `rom`
  records containing `sha1` and/or `crc`.
- Logiqx XML `.dat` or `.xml` files with `game`/`machine`, `description`, and
  `rom` elements.
- PixelDeck JSON catalogs in this form:

```json
{
  "games": [
    {
      "title": "Example Game (USA)",
      "sha1": "0123456789ABCDEF0123456789ABCDEF01234567",
      "crc32": "89ABCDEF"
    }
  ]
}
```

At least one valid hash is required per entry. Catalogs contain names and
fingerprints, not game data. PixelDeck does not download a catalog or contact a
metadata service at runtime.

For NES images, PixelDeck tests the hash of the complete file and the exact
headerless PRG/CHR payload. Trainers and unrelated trailing bytes are excluded
from the payload form. For SNES images, it also tests the image with a 512-byte
copier header removed. This allows a single local catalog to recognize the
common dump layouts without modifying the user's files.

## Incremental cache

Resolved titles are stored in:

```text
Games/.pixeldeck/title-cache.json
```

The cache records the relative path, file size, last-write timestamp, active
catalog revision, and resolved title. An unchanged file is not read or hashed
again. A new or modified game is inspected on the next library refresh. Adding
or changing a catalog invalidates cached fallback names so the library updates
immediately. Deleting the cache is safe; PixelDeck recreates it.
