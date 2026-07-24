namespace PixelDeck.Emulation.Snes;

internal sealed class SnesPpu
{
    public const int Width = 256;
    public const int Height = 224;

    private const byte BackdropLayer = 5;

    private readonly byte[] _vram = new byte[65_536];
    private readonly byte[] _cgram = new byte[512];
    private readonly byte[] _oam = new byte[544];
    private readonly uint[] _frameBuffer = new uint[Width * Height];
    private readonly byte[] _bgScreen = new byte[4];
    private readonly ushort[] _bgHorizontalScroll = new ushort[4];
    private readonly ushort[] _bgVerticalScroll = new ushort[4];
    private readonly byte[] _scrollLow = new byte[8];
    private readonly bool[] _scrollHighWrite = new bool[8];
    private readonly byte[] _windowSelection = new byte[3];
    private readonly byte[] _windowPositions = new byte[4];
    private readonly byte[] _windowLogic = new byte[2];
    private readonly ushort[] _mainLineColors = new ushort[Width];
    private readonly ushort[] _subLineColors = new ushort[Width];
    private readonly int[] _mainLinePriorities = new int[Width];
    private readonly int[] _subLinePriorities = new int[Width];
    private readonly byte[] _mainLineLayers = new byte[Width];
    private readonly byte[] _subLineLayers = new byte[Width];

    private byte _brightness;
    private bool _forcedBlank = true;
    private byte _objectSizeAndBase;
    private byte _backgroundMode;
    private byte _mosaic;
    private byte _bg12TileBase;
    private byte _bg34TileBase;
    private byte _mainScreen;
    private byte _subScreen;
    private byte _mainWindow;
    private byte _subWindow;
    private byte _colorMathControl;
    private byte _colorMathDesignation;
    private ushort _fixedColor;
    private byte _screenMode;
    private byte _vramIncrementMode;
    private ushort _vramAddress;
    private ushort _cgramAddress;
    private bool _cgramHighWrite;
    private byte _cgramLow;
    private ushort _oamAddress;
    private byte _mode7Control;
    private byte _mode7Latch;
    private short _mode7A;
    private short _mode7B;
    private short _mode7C;
    private short _mode7D;
    private short _mode7CenterX;
    private short _mode7CenterY;
    private short _mode7HorizontalOffset;
    private short _mode7VerticalOffset;

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
            case 0x2106:
                _mosaic = value;
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
            case 0x211A:
                _mode7Control = value;
                break;
            case 0x211B:
                _mode7A = WriteMode7Word(value);
                break;
            case 0x211C:
                _mode7B = WriteMode7Word(value);
                break;
            case 0x211D:
                _mode7C = WriteMode7Word(value);
                break;
            case 0x211E:
                _mode7D = WriteMode7Word(value);
                break;
            case 0x211F:
                _mode7CenterX = WriteMode7Word(value);
                break;
            case 0x2120:
                _mode7CenterY = WriteMode7Word(value);
                break;
            case 0x2121:
                _cgramAddress = (ushort)(value * 2);
                _cgramHighWrite = false;
                break;
            case 0x2122:
                WriteCgram(value);
                break;
            case >= 0x2123 and <= 0x2125:
                _windowSelection[address - 0x2123] = value;
                break;
            case >= 0x2126 and <= 0x2129:
                _windowPositions[address - 0x2126] = value;
                break;
            case >= 0x212A and <= 0x212B:
                _windowLogic[address - 0x212A] = value;
                break;
            case 0x212C:
                _mainScreen = value;
                break;
            case 0x212D:
                _subScreen = value;
                break;
            case 0x212E:
                _mainWindow = value;
                break;
            case 0x212F:
                _subWindow = value;
                break;
            case 0x2130:
                _colorMathControl = value;
                break;
            case 0x2131:
                _colorMathDesignation = value;
                break;
            case 0x2132:
                WriteFixedColor(value);
                break;
            case 0x2133:
                _screenMode = value;
                break;
        }
    }

    public byte ReadRegister(ushort address)
    {
        var multiplication = _mode7A * (_mode7B >> 8);
        return address switch
        {
            0x2134 => (byte)multiplication,
            0x2135 => (byte)(multiplication >> 8),
            0x2136 => (byte)(multiplication >> 16),
            0x2138 => _oam[(_oamAddress++) % _oam.Length],
            0x2139 => ReadVram(highByte: false),
            0x213A => ReadVram(highByte: true),
            0x213B => _cgram[_cgramAddress++ & 0x01FF],
            0x213E => 0x01,
            0x213F => 0x03,
            _ => 0
        };
    }

    public void RenderFrame()
    {
        for (var y = 0; y < Height; y++)
        {
            RenderScanline(y);
        }
    }

    public void RenderScanline(int y)
    {
        if (y is < 0 or >= Height)
        {
            return;
        }

        if (_forcedBlank)
        {
            Array.Fill(_frameBuffer, 0xFF000000u, y * Width, Width);
            return;
        }

        var backdrop = ReadColor15(0);
        Array.Fill(_mainLineColors, backdrop);
        Array.Fill(_subLineColors, backdrop);
        Array.Fill(_mainLinePriorities, -1);
        Array.Fill(_subLinePriorities, -1);
        Array.Fill(_mainLineLayers, BackdropLayer);
        Array.Fill(_subLineLayers, BackdropLayer);

        var mode = _backgroundMode & 0x07;
        if (mode == 7)
        {
            RenderMode7Line(y, mainScreen: true);
            RenderMode7Line(y, mainScreen: false);
        }
        else
        {
            for (var background = 3; background >= 0; background--)
            {
                if (GetBitsPerPixel(mode, background) == 0)
                {
                    continue;
                }

                RenderBackgroundLine(background, mode, y, mainScreen: true);
                RenderBackgroundLine(background, mode, y, mainScreen: false);
            }
        }

        RenderSpritesLine(y, mainScreen: true);
        RenderSpritesLine(y, mainScreen: false);

        var outputOffset = y * Width;
        for (var x = 0; x < Width; x++)
        {
            var colorWindowInside = IsWindowInside(layer: 5, x);
            var mainColor = _mainLineColors[x];
            if (ApplyColorWindowRule((_colorMathControl >> 6) & 3, colorWindowInside))
            {
                mainColor = 0;
            }

            var mathPrevented = ApplyColorWindowRule((_colorMathControl >> 4) & 3, colorWindowInside);
            var layer = _mainLineLayers[x];
            if (!mathPrevented && (_colorMathDesignation & (1 << layer)) != 0)
            {
                var secondColor = (_colorMathControl & 0x02) != 0
                    ? _subLineColors[x]
                    : _fixedColor;
                mainColor = ApplyColorMath(
                    mainColor,
                    secondColor,
                    subtract: (_colorMathDesignation & 0x80) != 0,
                    half: (_colorMathDesignation & 0x40) != 0);
            }

            _frameBuffer[outputOffset + x] = ExpandColor(mainColor);
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
        writer.Write(_mosaic);
        writer.Write(_bg12TileBase);
        writer.Write(_bg34TileBase);
        writer.Write(_mainScreen);
        writer.Write(_subScreen);
        writer.Write(_mainWindow);
        writer.Write(_subWindow);
        writer.Write(_colorMathControl);
        writer.Write(_colorMathDesignation);
        writer.Write(_fixedColor);
        writer.Write(_screenMode);
        writer.Write(_vramIncrementMode);
        writer.Write(_vramAddress);
        writer.Write(_cgramAddress);
        writer.Write(_cgramHighWrite);
        writer.Write(_cgramLow);
        writer.Write(_oamAddress);
        writer.Write(_mode7Control);
        writer.Write(_mode7Latch);
        writer.Write(_mode7A);
        writer.Write(_mode7B);
        writer.Write(_mode7C);
        writer.Write(_mode7D);
        writer.Write(_mode7CenterX);
        writer.Write(_mode7CenterY);
        writer.Write(_mode7HorizontalOffset);
        writer.Write(_mode7VerticalOffset);
        writer.Write(RegisterWriteCount);
        writer.Write(_bgScreen);
        foreach (var value in _bgHorizontalScroll) writer.Write(value);
        foreach (var value in _bgVerticalScroll) writer.Write(value);
        writer.Write(_scrollLow);
        foreach (var value in _scrollHighWrite) writer.Write(value);
        writer.Write(_windowSelection);
        writer.Write(_windowPositions);
        writer.Write(_windowLogic);
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
        _mosaic = reader.ReadByte();
        _bg12TileBase = reader.ReadByte();
        _bg34TileBase = reader.ReadByte();
        _mainScreen = reader.ReadByte();
        _subScreen = reader.ReadByte();
        _mainWindow = reader.ReadByte();
        _subWindow = reader.ReadByte();
        _colorMathControl = reader.ReadByte();
        _colorMathDesignation = reader.ReadByte();
        _fixedColor = reader.ReadUInt16();
        _screenMode = reader.ReadByte();
        _vramIncrementMode = reader.ReadByte();
        _vramAddress = reader.ReadUInt16();
        _cgramAddress = reader.ReadUInt16();
        _cgramHighWrite = reader.ReadBoolean();
        _cgramLow = reader.ReadByte();
        _oamAddress = reader.ReadUInt16();
        _mode7Control = reader.ReadByte();
        _mode7Latch = reader.ReadByte();
        _mode7A = reader.ReadInt16();
        _mode7B = reader.ReadInt16();
        _mode7C = reader.ReadInt16();
        _mode7D = reader.ReadInt16();
        _mode7CenterX = reader.ReadInt16();
        _mode7CenterY = reader.ReadInt16();
        _mode7HorizontalOffset = reader.ReadInt16();
        _mode7VerticalOffset = reader.ReadInt16();
        RegisterWriteCount = reader.ReadInt64();
        reader.ReadExactly(_bgScreen);
        for (var index = 0; index < 4; index++) _bgHorizontalScroll[index] = reader.ReadUInt16();
        for (var index = 0; index < 4; index++) _bgVerticalScroll[index] = reader.ReadUInt16();
        reader.ReadExactly(_scrollLow);
        for (var index = 0; index < 8; index++) _scrollHighWrite[index] = reader.ReadBoolean();
        reader.ReadExactly(_windowSelection);
        reader.ReadExactly(_windowPositions);
        reader.ReadExactly(_windowLogic);
        RenderFrame();
    }

    private void RenderBackgroundLine(int background, int mode, int y, bool mainScreen)
    {
        var screenDesignation = mainScreen ? _mainScreen : _subScreen;
        if ((screenDesignation & (1 << background)) == 0)
        {
            return;
        }

        var bitsPerPixel = GetBitsPerPixel(mode, background);
        var colors = mainScreen ? _mainLineColors : _subLineColors;
        var priorities = mainScreen ? _mainLinePriorities : _subLinePriorities;
        var layers = mainScreen ? _mainLineLayers : _subLineLayers;
        var windowDesignation = mainScreen ? _mainWindow : _subWindow;

        for (var x = 0; x < Width; x++)
        {
            if ((windowDesignation & (1 << background)) != 0 && IsWindowInside(background, x))
            {
                continue;
            }

            if (!TryReadBackgroundPixel(
                    background,
                    mode,
                    bitsPerPixel,
                    x,
                    y,
                    out var color,
                    out var highPriority))
            {
                continue;
            }

            var priority = GetBackgroundPriority(mode, background, highPriority);
            if (priority >= priorities[x])
            {
                priorities[x] = priority;
                colors[x] = color;
                layers[x] = (byte)background;
            }
        }
    }

    private bool TryReadBackgroundPixel(
        int background,
        int mode,
        int bitsPerPixel,
        int screenX,
        int screenY,
        out ushort color,
        out bool highPriority)
    {
        var mosaicEnabled = (_mosaic & (1 << background)) != 0;
        var mosaicSize = (_mosaic >> 4) + 1;
        if (mosaicEnabled && mosaicSize > 1)
        {
            screenX -= screenX % mosaicSize;
            screenY -= screenY % mosaicSize;
        }

        var horizontalScale = mode is 5 or 6 ? 2 : 1;
        var largeTiles = (_backgroundMode & (0x10 << background)) != 0;
        var logicalTileSize = largeTiles ? 16 : 8;
        var worldX = ((screenX * horizontalScale) + _bgHorizontalScroll[background]) & 0x03FF;
        // The SNES background pipeline fetches the first visible line from
        // vertical coordinate VOFS + 1.
        var worldY = (screenY + _bgVerticalScroll[background] + 1) & 0x03FF;
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

        var paletteBase = bitsPerPixel switch
        {
            8 => 0,
            4 => palette * 16,
            _ when mode == 0 => (background * 32) + (palette * 4),
            _ => palette * 4
        };
        color = ReadColor15(paletteBase + pixel);
        return true;
    }

    private void RenderMode7Line(int screenY, bool mainScreen)
    {
        var extendedBackground = (_screenMode & 0x40) != 0;
        var maximumBackground = extendedBackground ? 1 : 0;
        var screenDesignation = mainScreen ? _mainScreen : _subScreen;
        var colors = mainScreen ? _mainLineColors : _subLineColors;
        var priorities = mainScreen ? _mainLinePriorities : _subLinePriorities;
        var layers = mainScreen ? _mainLineLayers : _subLineLayers;
        var windowDesignation = mainScreen ? _mainWindow : _subWindow;

        for (var background = maximumBackground; background >= 0; background--)
        {
            if ((screenDesignation & (1 << background)) == 0)
            {
                continue;
            }

            for (var screenX = 0; screenX < Width; screenX++)
            {
                if ((windowDesignation & (1 << background)) != 0 &&
                    IsWindowInside(background, screenX))
                {
                    continue;
                }

                if (!TryReadMode7Pixel(
                        background,
                        screenX,
                        screenY,
                        out var color,
                        out var highPriority))
                {
                    continue;
                }

                var priority = background == 0
                    ? (highPriority ? 8 : 4)
                    : (highPriority ? 9 : 3);
                if (priority >= priorities[screenX])
                {
                    priorities[screenX] = priority;
                    colors[screenX] = color;
                    layers[screenX] = (byte)background;
                }
            }
        }
    }

    private bool TryReadMode7Pixel(
        int background,
        int screenX,
        int screenY,
        out ushort color,
        out bool highPriority)
    {
        if ((_mode7Control & 0x01) != 0) screenX = Width - 1 - screenX;
        if ((_mode7Control & 0x02) != 0) screenY = 255 - screenY;

        var horizontalOffset = SignExtend13(_mode7HorizontalOffset);
        var verticalOffset = SignExtend13(_mode7VerticalOffset);
        var centerX = SignExtend13(_mode7CenterX);
        var centerY = SignExtend13(_mode7CenterY);
        var relativeX = SignExtend13(screenX + horizontalOffset - centerX);
        var relativeY = SignExtend13(screenY + verticalOffset - centerY);
        var transformedX = ((_mode7A * relativeX) + (_mode7B * relativeY) + (centerX << 8)) >> 8;
        var transformedY = ((_mode7C * relativeX) + (_mode7D * relativeY) + (centerY << 8)) >> 8;

        var outside = transformedX is < 0 or >= 1024 || transformedY is < 0 or >= 1024;
        var repeat = (_mode7Control >> 6) & 3;
        if (outside && repeat is 1 or 2)
        {
            color = 0;
            highPriority = false;
            return false;
        }

        var tile = 0;
        if (!outside || repeat != 3)
        {
            var mapX = transformedX & 0x03FF;
            var mapY = transformedY & 0x03FF;
            var mapAddress = ((((mapY >> 3) * 128) + (mapX >> 3)) * 2) & 0xFFFF;
            tile = _vram[mapAddress];
        }

        var pixelX = transformedX & 7;
        var pixelY = transformedY & 7;
        var characterAddress = (((tile * 64) + (pixelY * 8) + pixelX) * 2 + 1) & 0xFFFF;
        var pixel = _vram[characterAddress];
        if (pixel == 0)
        {
            color = 0;
            highPriority = false;
            return false;
        }

        if (background == 1)
        {
            highPriority = (pixel & 0x80) != 0;
            pixel &= 0x7F;
            if (pixel == 0)
            {
                color = 0;
                return false;
            }
        }
        else
        {
            highPriority = false;
        }

        color = ReadColor15(pixel);
        return true;
    }

    private void RenderSpritesLine(int y, bool mainScreen)
    {
        var screenDesignation = mainScreen ? _mainScreen : _subScreen;
        if ((screenDesignation & 0x10) == 0)
        {
            return;
        }

        var colors = mainScreen ? _mainLineColors : _subLineColors;
        var priorities = mainScreen ? _mainLinePriorities : _subLinePriorities;
        var layers = mainScreen ? _mainLineLayers : _subLineLayers;
        var windowDesignation = mainScreen ? _mainWindow : _subWindow;
        var sizes = GetObjectSizes((_objectSizeAndBase >> 5) & 7);
        var characterBase = (_objectSizeAndBase & 7) * 0x4000;
        var nameSelectOffset = (((_objectSizeAndBase >> 3) & 3) + 1) * 0x2000;

        for (var objectIndex = 127; objectIndex >= 0; objectIndex--)
        {
            var lowIndex = objectIndex * 4;
            var highBits = _oam[512 + (objectIndex / 4)];
            var shift = (objectIndex & 3) * 2;
            var x = _oam[lowIndex] | (((highBits >> shift) & 1) << 8);
            if (x >= 256) x -= 512;
            var objectY = (int)_oam[lowIndex + 1];
            if (objectY >= 224) objectY -= 256;
            var large = ((highBits >> (shift + 1)) & 1) != 0;
            var size = large ? sizes.Large : sizes.Small;
            var localY = y - objectY;
            if (localY is < 0 || localY >= size)
            {
                continue;
            }

            var baseCharacter = _oam[lowIndex + 2];
            var attributes = _oam[lowIndex + 3];
            var nameSelect = attributes & 1;
            var palette = (attributes >> 1) & 7;
            var priority = GetObjectPriority((attributes >> 4) & 3);
            var horizontalFlip = (attributes & 0x40) != 0;
            var verticalFlip = (attributes & 0x80) != 0;
            var sourceY = verticalFlip ? size - 1 - localY : localY;
            var objectBase = (characterBase + (nameSelect != 0 ? nameSelectOffset : 0)) & 0xFFFF;

            for (var localX = 0; localX < size; localX++)
            {
                var outputX = x + localX;
                if (outputX is < 0 or >= Width)
                {
                    continue;
                }

                if ((windowDesignation & 0x10) != 0 && IsWindowInside(layer: 4, outputX))
                {
                    continue;
                }

                var sourceX = horizontalFlip ? size - 1 - localX : localX;
                var character = (baseCharacter + (sourceX / 8) + ((sourceY / 8) * 16)) & 0xFF;
                var pixel = ReadPlanarPixel(
                    (objectBase + (character * 32)) & 0xFFFF,
                    4,
                    sourceX & 7,
                    sourceY & 7);
                if (pixel == 0)
                {
                    continue;
                }

                if (priority >= priorities[outputX])
                {
                    priorities[outputX] = priority;
                    colors[outputX] = ReadColor15(128 + (palette * 16) + pixel);
                    layers[outputX] = 4;
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

    private ushort ReadColor15(int colorIndex)
    {
        var address = (colorIndex * 2) & 0x01FF;
        return (ushort)((_cgram[address] | (_cgram[(address + 1) & 0x01FF] << 8)) & 0x7FFF);
    }

    private uint ExpandColor(ushort color)
    {
        var red = ((color & 0x1F) * 255 / 31) * _brightness / 15;
        var green = (((color >> 5) & 0x1F) * 255 / 31) * _brightness / 15;
        var blue = (((color >> 10) & 0x1F) * 255 / 31) * _brightness / 15;
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
        if (register == 0)
        {
            _mode7HorizontalOffset = WriteMode7Word(value);
        }
        else if (register == 1)
        {
            _mode7VerticalOffset = WriteMode7Word(value);
        }

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

    private short WriteMode7Word(byte value)
    {
        var result = (short)((value << 8) | _mode7Latch);
        _mode7Latch = value;
        return result;
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

    private void WriteFixedColor(byte value)
    {
        var component = (ushort)(value & 0x1F);
        if ((value & 0x20) != 0)
        {
            _fixedColor = (ushort)((_fixedColor & ~0x001F) | component);
        }

        if ((value & 0x40) != 0)
        {
            _fixedColor = (ushort)((_fixedColor & ~0x03E0) | (component << 5));
        }

        if ((value & 0x80) != 0)
        {
            _fixedColor = (ushort)((_fixedColor & ~0x7C00) | (component << 10));
        }
    }

    private bool IsWindowInside(int layer, int x)
    {
        var selection = layer switch
        {
            0 => _windowSelection[0] & 0x0F,
            1 => _windowSelection[0] >> 4,
            2 => _windowSelection[1] & 0x0F,
            3 => _windowSelection[1] >> 4,
            4 => _windowSelection[2] & 0x0F,
            _ => _windowSelection[2] >> 4
        };
        var windowOneEnabled = (selection & 0x02) != 0;
        var windowTwoEnabled = (selection & 0x08) != 0;
        if (!windowOneEnabled && !windowTwoEnabled)
        {
            return false;
        }

        var windowOne = x >= _windowPositions[0] && x <= _windowPositions[1];
        var windowTwo = x >= _windowPositions[2] && x <= _windowPositions[3];
        if ((selection & 0x01) != 0) windowOne = !windowOne;
        if ((selection & 0x04) != 0) windowTwo = !windowTwo;
        if (!windowOneEnabled) return windowTwo;
        if (!windowTwoEnabled) return windowOne;

        var logic = layer switch
        {
            <= 3 => (_windowLogic[0] >> (layer * 2)) & 3,
            4 => _windowLogic[1] & 3,
            _ => (_windowLogic[1] >> 2) & 3
        };
        return logic switch
        {
            0 => windowOne || windowTwo,
            1 => windowOne && windowTwo,
            2 => windowOne ^ windowTwo,
            _ => windowOne == windowTwo
        };
    }

    private static bool ApplyColorWindowRule(int rule, bool inside) => rule switch
    {
        0 => false,
        1 => !inside,
        2 => inside,
        _ => true
    };

    private static ushort ApplyColorMath(ushort first, ushort second, bool subtract, bool half)
    {
        var red = ApplyColorMathComponent(first & 0x1F, second & 0x1F, subtract, half);
        var green = ApplyColorMathComponent((first >> 5) & 0x1F, (second >> 5) & 0x1F, subtract, half);
        var blue = ApplyColorMathComponent((first >> 10) & 0x1F, (second >> 10) & 0x1F, subtract, half);
        return (ushort)(red | (green << 5) | (blue << 10));
    }

    private static int ApplyColorMathComponent(int first, int second, bool subtract, bool half)
    {
        var value = subtract ? Math.Max(0, first - second) : Math.Min(31, first + second);
        return half ? value >> 1 : value;
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

    private static int GetBitsPerPixel(int mode, int background) => mode switch
    {
        0 => 2,
        1 => background switch { 0 or 1 => 4, 2 => 2, _ => 0 },
        2 => background <= 1 ? 4 : 0,
        3 => background switch { 0 => 8, 1 => 4, _ => 0 },
        4 => background switch { 0 => 8, 1 => 2, _ => 0 },
        5 => background switch { 0 => 4, 1 => 2, _ => 0 },
        6 => background == 0 ? 4 : 0,
        _ => 0
    };

    private int GetBackgroundPriority(int mode, int background, bool high) => mode switch
    {
        0 => background switch
        {
            0 => high ? 11 : 8,
            1 => high ? 10 : 7,
            2 => high ? 6 : 2,
            _ => high ? 5 : 1
        },
        1 => background switch
        {
            0 => high ? 8 : 5,
            1 => high ? 7 : 4,
            2 => high && (_backgroundMode & 0x08) != 0 ? 10 : high ? 3 : 1,
            _ => -1
        },
        _ => background switch
        {
            0 => high ? 8 : 5,
            1 => high ? 7 : 3,
            2 => high ? 6 : 2,
            _ => high ? 4 : 1
        }
    };

    private static int GetObjectPriority(int priority) => priority switch
    {
        0 => 2,
        1 => 6,
        2 => 9,
        _ => 12
    };

    private static int SignExtend13(int value)
    {
        value &= 0x1FFF;
        return (value & 0x1000) != 0 ? value | ~0x1FFF : value;
    }

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
