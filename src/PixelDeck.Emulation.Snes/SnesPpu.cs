namespace PixelDeck.Emulation.Snes;

internal sealed class SnesPpu
{
    public const int Width = 256;
    public const int Height = 224;

    private readonly byte[] _vram = new byte[65_536];
    private readonly byte[] _cgram = new byte[512];
    private readonly byte[] _oam = new byte[544];
    private readonly uint[] _frameBuffer = new uint[Width * Height];
    private readonly int[] _pixelPriority = new int[Width * Height];
    private readonly byte[] _bgScreen = new byte[4];
    private readonly ushort[] _bgHorizontalScroll = new ushort[4];
    private readonly ushort[] _bgVerticalScroll = new ushort[4];
    private readonly byte[] _scrollLow = new byte[8];
    private readonly bool[] _scrollHighWrite = new bool[8];

    private byte _brightness;
    private bool _forcedBlank = true;
    private byte _objectSizeAndBase;
    private byte _backgroundMode;
    private byte _bg12TileBase;
    private byte _bg34TileBase;
    private byte _mainScreen;
    private byte _vramIncrementMode;
    private ushort _vramAddress;
    private ushort _cgramAddress;
    private bool _cgramHighWrite;
    private byte _cgramLow;
    private ushort _oamAddress;

    public ReadOnlySpan<uint> FrameBuffer => _frameBuffer;

    public bool ForcedBlank => _forcedBlank;

    public byte Brightness => _brightness;

    public byte BackgroundMode => (byte)(_backgroundMode & 0x07);

    public byte MainScreen => _mainScreen;

    public long RegisterWriteCount { get; private set; }

    public int NonZeroVramBytes => _vram.Count(value => value != 0);

    public int NonZeroCgramBytes => _cgram.Count(value => value != 0);

    public int NonZeroOamBytes => _oam.Count(value => value != 0);

    public void WriteRegister(ushort address, byte value)
    {
        RegisterWriteCount++;
        switch (address)
        {
            case 0x2100:
                _brightness = (byte)(value & 0x0F);
                _forcedBlank = (value & 0x80) != 0;
                break;
            case 0x2101:
                _objectSizeAndBase = value;
                break;
            case 0x2102:
                _oamAddress = (ushort)((_oamAddress & 0x100) | value);
                break;
            case 0x2103:
                _oamAddress = (ushort)((_oamAddress & 0x0FF) | ((value & 1) << 8));
                break;
            case 0x2104:
                _oam[(_oamAddress++) % _oam.Length] = value;
                break;
            case 0x2105:
                _backgroundMode = value;
                break;
            case >= 0x2107 and <= 0x210A:
                _bgScreen[address - 0x2107] = value;
                break;
            case 0x210B:
                _bg12TileBase = value;
                break;
            case 0x210C:
                _bg34TileBase = value;
                break;
            case >= 0x210D and <= 0x2114:
                WriteScroll(address - 0x210D, value);
                break;
            case 0x2115:
                _vramIncrementMode = value;
                break;
            case 0x2116:
                _vramAddress = (ushort)((_vramAddress & 0xFF00) | value);
                break;
            case 0x2117:
                _vramAddress = (ushort)((_vramAddress & 0x00FF) | (value << 8));
                break;
            case 0x2118:
                WriteVram(highByte: false, value);
                break;
            case 0x2119:
                WriteVram(highByte: true, value);
                break;
            case 0x2121:
                _cgramAddress = (ushort)(value * 2);
                _cgramHighWrite = false;
                break;
            case 0x2122:
                WriteCgram(value);
                break;
            case 0x212C:
                _mainScreen = value;
                break;
        }
    }

    public byte ReadRegister(ushort address) => address switch
    {
        0x2138 => _oam[(_oamAddress++) % _oam.Length],
        0x2139 => ReadVram(highByte: false),
        0x213A => ReadVram(highByte: true),
        0x213B => _cgram[_cgramAddress++ & 0x01FF],
        0x213E => 0x01,
        0x213F => 0x03,
        _ => 0
    };

    public void RenderFrame()
    {
        var backdrop = _forcedBlank ? 0xFF000000u : ReadColor(0);
        Array.Fill(_frameBuffer, backdrop);
        Array.Fill(_pixelPriority, -1);
        if (_forcedBlank)
        {
            return;
        }

        var mode = _backgroundMode & 0x07;
        if (mode is 0 or 1)
        {
            var backgroundCount = mode == 0 ? 4 : 3;
            for (var background = backgroundCount - 1; background >= 0; background--)
            {
                if ((_mainScreen & (1 << background)) != 0)
                {
                    RenderBackground(background, mode);
                }
            }
        }

        if ((_mainScreen & 0x10) != 0)
        {
            RenderSprites();
        }
    }

    internal void SaveState(BinaryWriter writer)
    {
        writer.Write(_vram);
        writer.Write(_cgram);
        writer.Write(_oam);
        writer.Write(_brightness);
        writer.Write(_forcedBlank);
        writer.Write(_objectSizeAndBase);
        writer.Write(_backgroundMode);
        writer.Write(_bg12TileBase);
        writer.Write(_bg34TileBase);
        writer.Write(_mainScreen);
        writer.Write(_vramIncrementMode);
        writer.Write(_vramAddress);
        writer.Write(_cgramAddress);
        writer.Write(_cgramHighWrite);
        writer.Write(_cgramLow);
        writer.Write(_oamAddress);
        writer.Write(RegisterWriteCount);
        writer.Write(_bgScreen);
        foreach (var value in _bgHorizontalScroll) writer.Write(value);
        foreach (var value in _bgVerticalScroll) writer.Write(value);
        writer.Write(_scrollLow);
        foreach (var value in _scrollHighWrite) writer.Write(value);
    }

    internal void LoadState(BinaryReader reader)
    {
        reader.ReadExactly(_vram);
        reader.ReadExactly(_cgram);
        reader.ReadExactly(_oam);
        _brightness = reader.ReadByte();
        _forcedBlank = reader.ReadBoolean();
        _objectSizeAndBase = reader.ReadByte();
        _backgroundMode = reader.ReadByte();
        _bg12TileBase = reader.ReadByte();
        _bg34TileBase = reader.ReadByte();
        _mainScreen = reader.ReadByte();
        _vramIncrementMode = reader.ReadByte();
        _vramAddress = reader.ReadUInt16();
        _cgramAddress = reader.ReadUInt16();
        _cgramHighWrite = reader.ReadBoolean();
        _cgramLow = reader.ReadByte();
        _oamAddress = reader.ReadUInt16();
        RegisterWriteCount = reader.ReadInt64();
        reader.ReadExactly(_bgScreen);
        for (var index = 0; index < 4; index++) _bgHorizontalScroll[index] = reader.ReadUInt16();
        for (var index = 0; index < 4; index++) _bgVerticalScroll[index] = reader.ReadUInt16();
        reader.ReadExactly(_scrollLow);
        for (var index = 0; index < 8; index++) _scrollHighWrite[index] = reader.ReadBoolean();
        RenderFrame();
    }

    private void RenderBackground(int background, int mode)
    {
        var bitsPerPixel = mode == 0 || background == 2 ? 2 : 4;
        var basePriority = (3 - background) * 2;
        var highPriorityBonus = mode == 1 && background == 2 && (_backgroundMode & 0x08) != 0 ? 8 : 4;

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (!TryReadBackgroundPixel(background, mode, bitsPerPixel, x, y, out var color, out var highPriority))
                {
                    continue;
                }

                var priority = basePriority + (highPriority ? highPriorityBonus : 0);
                var outputIndex = (y * Width) + x;
                if (priority >= _pixelPriority[outputIndex])
                {
                    _pixelPriority[outputIndex] = priority;
                    _frameBuffer[outputIndex] = ReadColor(color);
                }
            }
        }
    }

    private bool TryReadBackgroundPixel(
        int background,
        int mode,
        int bitsPerPixel,
        int screenX,
        int screenY,
        out int color,
        out bool highPriority)
    {
        var largeTiles = (_backgroundMode & (0x10 << background)) != 0;
        var logicalTileSize = largeTiles ? 16 : 8;
        var worldX = (screenX + _bgHorizontalScroll[background]) & 0x03FF;
        var worldY = (screenY + _bgVerticalScroll[background]) & 0x03FF;
        var logicalTileX = worldX / logicalTileSize;
        var logicalTileY = worldY / logicalTileSize;
        var screenSize = _bgScreen[background] & 0x03;
        var widthInTiles = screenSize is 1 or 3 ? 64 : 32;
        var heightInTiles = screenSize is 2 or 3 ? 64 : 32;
        logicalTileX %= widthInTiles;
        logicalTileY %= heightInTiles;

        var screenBlockX = logicalTileX / 32;
        var screenBlockY = logicalTileY / 32;
        var screenBlock = screenSize switch
        {
            1 => screenBlockX,
            2 => screenBlockY,
            3 => (screenBlockY * 2) + screenBlockX,
            _ => 0
        };
        var mapBase = (_bgScreen[background] & 0xFC) << 9;
        var mapIndex = (mapBase + (screenBlock * 0x800) +
                        ((((logicalTileY & 31) * 32) + (logicalTileX & 31)) * 2)) & 0xFFFF;
        var entry = (ushort)(_vram[mapIndex] | (_vram[(mapIndex + 1) & 0xFFFF] << 8));
        var character = entry & 0x03FF;
        var palette = (entry >> 10) & 0x07;
        highPriority = (entry & 0x2000) != 0;
        var horizontalFlip = (entry & 0x4000) != 0;
        var verticalFlip = (entry & 0x8000) != 0;
        var pixelX = worldX % logicalTileSize;
        var pixelY = worldY % logicalTileSize;
        if (horizontalFlip) pixelX = logicalTileSize - 1 - pixelX;
        if (verticalFlip) pixelY = logicalTileSize - 1 - pixelY;

        if (largeTiles)
        {
            character = (character + (pixelX / 8) + ((pixelY / 8) * 16)) & 0x03FF;
            pixelX &= 7;
            pixelY &= 7;
        }

        var tileBase = GetBackgroundTileBase(background);
        var tileByteLength = bitsPerPixel * 8;
        var tileAddress = (tileBase + (character * tileByteLength)) & 0xFFFF;
        var pixel = ReadPlanarPixel(tileAddress, bitsPerPixel, pixelX, pixelY);
        if (pixel == 0)
        {
            color = 0;
            return false;
        }

        var paletteBase = bitsPerPixel == 4
            ? palette * 16
            : mode == 0
                ? (background * 32) + (palette * 4)
                : palette * 4;
        color = paletteBase + pixel;
        return true;
    }

    private void RenderSprites()
    {
        var sizes = GetObjectSizes((_objectSizeAndBase >> 5) & 7);
        var characterBase = (_objectSizeAndBase & 7) * 0x4000;

        for (var objectIndex = 127; objectIndex >= 0; objectIndex--)
        {
            var lowIndex = objectIndex * 4;
            var highBits = _oam[512 + (objectIndex / 4)];
            var shift = (objectIndex & 3) * 2;
            var x = _oam[lowIndex] | (((highBits >> shift) & 1) << 8);
            if (x >= 256) x -= 512;
            var y = (int)_oam[lowIndex + 1];
            if (y >= 224) y -= 256;
            var large = ((highBits >> (shift + 1)) & 1) != 0;
            var size = large ? sizes.Large : sizes.Small;
            var baseCharacter = _oam[lowIndex + 2];
            var attributes = _oam[lowIndex + 3];
            var nameSelect = attributes & 1;
            var palette = (attributes >> 1) & 7;
            var priority = ((attributes >> 4) & 3) * 3 + 3;
            var horizontalFlip = (attributes & 0x40) != 0;
            var verticalFlip = (attributes & 0x80) != 0;
            var objectBase = (characterBase + (nameSelect != 0 ? 0x2000 : 0)) & 0xFFFF;

            for (var localY = 0; localY < size; localY++)
            {
                var outputY = y + localY;
                if (outputY is < 0 or >= Height) continue;
                var sourceY = verticalFlip ? size - 1 - localY : localY;

                for (var localX = 0; localX < size; localX++)
                {
                    var outputX = x + localX;
                    if (outputX is < 0 or >= Width) continue;
                    var sourceX = horizontalFlip ? size - 1 - localX : localX;
                    var character = (baseCharacter + (sourceX / 8) + ((sourceY / 8) * 16)) & 0xFF;
                    var pixel = ReadPlanarPixel((objectBase + (character * 32)) & 0xFFFF, 4, sourceX & 7, sourceY & 7);
                    if (pixel == 0) continue;

                    var outputIndex = (outputY * Width) + outputX;
                    if (priority >= _pixelPriority[outputIndex])
                    {
                        _pixelPriority[outputIndex] = priority;
                        _frameBuffer[outputIndex] = ReadColor(128 + (palette * 16) + pixel);
                    }
                }
            }
        }
    }

    private int ReadPlanarPixel(int tileAddress, int bitsPerPixel, int x, int y)
    {
        var bit = 7 - x;
        var pixel = 0;
        for (var plane = 0; plane < bitsPerPixel; plane++)
        {
            var planePair = plane / 2;
            var planeAddress = (tileAddress + (planePair * 16) + (y * 2) + (plane & 1)) & 0xFFFF;
            pixel |= ((_vram[planeAddress] >> bit) & 1) << plane;
        }

        return pixel;
    }

    private uint ReadColor(int colorIndex)
    {
        var address = (colorIndex * 2) & 0x01FF;
        var color = _cgram[address] | (_cgram[(address + 1) & 0x01FF] << 8);
        var brightness = _brightness;
        var red = ((color & 0x1F) * 255 / 31) * brightness / 15;
        var green = (((color >> 5) & 0x1F) * 255 / 31) * brightness / 15;
        var blue = (((color >> 10) & 0x1F) * 255 / 31) * brightness / 15;
        return 0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | (uint)blue;
    }

    private int GetBackgroundTileBase(int background)
    {
        var nibble = background switch
        {
            0 => _bg12TileBase & 0x0F,
            1 => _bg12TileBase >> 4,
            2 => _bg34TileBase & 0x0F,
            _ => _bg34TileBase >> 4
        };
        return (nibble * 0x2000) & 0xFFFF;
    }

    private void WriteScroll(int register, byte value)
    {
        if (!_scrollHighWrite[register])
        {
            _scrollLow[register] = value;
            _scrollHighWrite[register] = true;
            return;
        }

        var scroll = (ushort)(((value << 8) | _scrollLow[register]) & 0x03FF);
        var background = register / 2;
        if ((register & 1) == 0)
        {
            _bgHorizontalScroll[background] = scroll;
        }
        else
        {
            _bgVerticalScroll[background] = scroll;
        }

        _scrollHighWrite[register] = false;
    }

    private void WriteVram(bool highByte, byte value)
    {
        var mappedAddress = RemapVramAddress(_vramAddress);
        _vram[((mappedAddress * 2) + (highByte ? 1 : 0)) & 0xFFFF] = value;
        if (((_vramIncrementMode & 0x80) != 0) == highByte)
        {
            _vramAddress += GetVramIncrement();
        }
    }

    private byte ReadVram(bool highByte)
    {
        var mappedAddress = RemapVramAddress(_vramAddress);
        var value = _vram[((mappedAddress * 2) + (highByte ? 1 : 0)) & 0xFFFF];
        if (((_vramIncrementMode & 0x80) != 0) == highByte)
        {
            _vramAddress += GetVramIncrement();
        }

        return value;
    }

    private void WriteCgram(byte value)
    {
        if (!_cgramHighWrite)
        {
            _cgramLow = value;
            _cgramHighWrite = true;
            return;
        }

        _cgram[_cgramAddress++ & 0x01FF] = _cgramLow;
        _cgram[_cgramAddress++ & 0x01FF] = (byte)(value & 0x7F);
        _cgramHighWrite = false;
    }

    private ushort RemapVramAddress(ushort address) => ((_vramIncrementMode >> 2) & 3) switch
    {
        1 => (ushort)((address & 0xFF00) | ((address & 0x001F) << 3) | ((address & 0x00E0) >> 5)),
        2 => (ushort)((address & 0xFE00) | ((address & 0x003F) << 3) | ((address & 0x01C0) >> 6)),
        3 => (ushort)((address & 0xFC00) | ((address & 0x007F) << 3) | ((address & 0x0380) >> 7)),
        _ => address
    };

    private ushort GetVramIncrement() => (_vramIncrementMode & 3) switch
    {
        1 => 32,
        2 or 3 => 128,
        _ => 1
    };

    private static (int Small, int Large) GetObjectSizes(int mode) => mode switch
    {
        0 => (8, 16),
        1 => (8, 32),
        2 => (8, 64),
        3 => (16, 32),
        4 => (16, 64),
        5 => (32, 64),
        6 => (16, 32),
        _ => (16, 32)
    };
}
