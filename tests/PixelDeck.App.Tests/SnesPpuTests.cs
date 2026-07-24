using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Tests;

public sealed class SnesPpuTests
{
    [Fact]
    public void Mode3RendersAnEightBitBackgroundPixel()
    {
        var ppu = new SnesPpu();
        SetDisplayOn(ppu);
        WriteColor(ppu, 1, 0x001F);
        WriteVramWord(ppu, 0x0000, 0x0000);
        // The first visible SNES scanline fetches tile row VOFS + 1.
        WriteVramWord(ppu, 0x1001, 0x0080);
        ppu.WriteRegister(0x2105, 0x03);
        ppu.WriteRegister(0x210B, 0x01);
        ppu.WriteRegister(0x212C, 0x01);

        ppu.RenderScanline(0);

        Assert.Equal(0xFFFF0000u, ppu.FrameBuffer[0]);
    }

    [Fact]
    public void Mode7AppliesItsAffineTileLookup()
    {
        var ppu = new SnesPpu();
        SetDisplayOn(ppu);
        WriteColor(ppu, 1, 0x03E0);
        WriteVramWord(ppu, 0x0000, 0x0001);
        WriteVramWord(ppu, 0x0040, 0x0100);
        ppu.WriteRegister(0x2105, 0x07);
        ppu.WriteRegister(0x212C, 0x01);
        WriteMode7Word(ppu, 0x211B, 0x0100);
        WriteMode7Word(ppu, 0x211C, 0x0000);
        WriteMode7Word(ppu, 0x211D, 0x0000);
        WriteMode7Word(ppu, 0x211E, 0x0100);
        WriteMode7Word(ppu, 0x211F, 0x0000);
        WriteMode7Word(ppu, 0x2120, 0x0000);
        WriteMode7Word(ppu, 0x210D, 0x0000);
        WriteMode7Word(ppu, 0x210E, 0x0000);

        ppu.RenderScanline(0);

        Assert.Equal(0xFF00FF00u, ppu.FrameBuffer[0]);
    }

    [Fact]
    public void ColorWindowAndFixedColorMathAreAppliedPerPixel()
    {
        var ppu = new SnesPpu();
        SetDisplayOn(ppu);
        WriteColor(ppu, 0, 0x001F);
        ppu.WriteRegister(0x2125, 0x20);
        ppu.WriteRegister(0x2126, 0);
        ppu.WriteRegister(0x2127, 127);
        ppu.WriteRegister(0x2130, 0x20);
        ppu.WriteRegister(0x2131, 0x20);
        ppu.WriteRegister(0x2132, 0x5F);

        ppu.RenderScanline(0);

        Assert.Equal(0xFFFF0000u, ppu.FrameBuffer[0]);
        Assert.Equal(0xFFFFFF00u, ppu.FrameBuffer[200]);
    }

    [Fact]
    public void Mode2UsesBg3OffsetsAfterTheFirstVisibleTileColumn()
    {
        var ppu = new SnesPpu();
        SetDisplayOn(ppu);
        WriteColor(ppu, 1, 0x001F);
        WriteColor(ppu, 2, 0x03E0);
        WriteColor(ppu, 3, 0x7C00);

        // BG1 map at word $0400, BG3 offset map at word $0800.
        ppu.WriteRegister(0x2107, 0x04);
        ppu.WriteRegister(0x2109, 0x08);
        ppu.WriteRegister(0x210B, 0x01);
        WriteVramWord(ppu, 0x0400, 0x0000);
        WriteVramWord(ppu, 0x0401, 0x0001);
        WriteVramWord(ppu, 0x0402, 0x0002);
        // BG3 entry zero applies to the second visible column and replaces
        // BG1's coarse horizontal scroll with eight pixels.
        WriteVramWord(ppu, 0x0800, 0x2008);
        WriteSolidFourBitTile(ppu, 0x1000, 0, 1);
        WriteSolidFourBitTile(ppu, 0x1000, 1, 2);
        WriteSolidFourBitTile(ppu, 0x1000, 2, 3);
        ppu.WriteRegister(0x2105, 0x02);
        ppu.WriteRegister(0x212C, 0x01);

        ppu.RenderScanline(0);

        Assert.Equal(0xFFFF0000u, ppu.FrameBuffer[0]);
        Assert.Equal(0xFF0000FFu, ppu.FrameBuffer[8]);
    }

    [Fact]
    public void Mode5TreatsNominalEightPixelCharactersAsHorizontalPairs()
    {
        var ppu = new SnesPpu();
        SetDisplayOn(ppu);
        WriteColor(ppu, 1, 0x001F);
        WriteColor(ppu, 2, 0x03E0);
        ppu.WriteRegister(0x210B, 0x01);
        WriteVramWord(ppu, 0x0000, 0x0000);
        WriteSolidFourBitTile(ppu, 0x1000, 0, 1);
        WriteSolidFourBitTile(ppu, 0x1000, 1, 2);
        ppu.WriteRegister(0x2105, 0x05);
        ppu.WriteRegister(0x212C, 0x01);

        ppu.RenderScanline(0);

        Assert.Equal(0xFFFF0000u, ppu.FrameBuffer[0]);
        Assert.Equal(0xFF00FF00u, ppu.FrameBuffer[4]);
    }

    [Fact]
    public void SingleHorizontalScrollWriteUpdatesTheHdmaHighByte()
    {
        var ppu = new SnesPpu();
        SetDisplayOn(ppu);
        WriteColor(ppu, 1, 0x001F);
        WriteColor(ppu, 2, 0x03E0);
        // A 64x32 BG1 map: the first tile of its second screen block is at
        // word $0400 and begins 256 pixels to the right.
        ppu.WriteRegister(0x2107, 0x01);
        ppu.WriteRegister(0x210B, 0x01);
        WriteVramWord(ppu, 0x0000, 0x0000);
        WriteVramWord(ppu, 0x0400, 0x0001);
        WriteSolidFourBitTile(ppu, 0x1000, 0, 1);
        WriteSolidFourBitTile(ppu, 0x1000, 1, 2);
        ppu.WriteRegister(0x2105, 0x01);
        ppu.WriteRegister(0x212C, 0x01);

        ppu.WriteRegister(0x210D, 0);
        ppu.WriteRegister(0x210D, 0);
        // HDMA commonly performs only one write to HOFS per scanline. This
        // changes the high byte immediately; it is not the first half of a
        // private two-write latch.
        ppu.WriteRegister(0x210D, 1);
        ppu.RenderScanline(0);

        Assert.Equal(0xFF00FF00u, ppu.FrameBuffer[0]);
    }

    [Fact]
    public void Mode3DirectColorBypassesCgram()
    {
        var ppu = new SnesPpu();
        SetDisplayOn(ppu);
        // If direct color were ignored, pixel seven would use this blue
        // CGRAM entry instead of the red value encoded by pixel/palette bits.
        WriteColor(ppu, 7, 0x7C00);
        WriteVramWord(ppu, 0x0000, 0x0400);
        WriteVramWord(ppu, 0x1001, 0x8080);
        WriteVramWord(ppu, 0x1009, 0x0080);
        ppu.WriteRegister(0x2105, 0x03);
        ppu.WriteRegister(0x210B, 0x01);
        ppu.WriteRegister(0x212C, 0x01);
        ppu.WriteRegister(0x2130, 0x01);

        ppu.RenderScanline(0);

        Assert.Equal(0xFFF60000u, ppu.FrameBuffer[0]);
    }

    [Fact]
    public void Mode7DirectColorBypassesCgram()
    {
        var ppu = new SnesPpu();
        SetDisplayOn(ppu);
        WriteColor(ppu, 7, 0x7C00);
        WriteVramWord(ppu, 0x0000, 0x0001);
        WriteVramWord(ppu, 0x0040, 0x0700);
        ppu.WriteRegister(0x2105, 0x07);
        ppu.WriteRegister(0x212C, 0x01);
        ppu.WriteRegister(0x2130, 0x01);
        WriteMode7Word(ppu, 0x211B, 0x0100);
        WriteMode7Word(ppu, 0x211C, 0x0000);
        WriteMode7Word(ppu, 0x211D, 0x0000);
        WriteMode7Word(ppu, 0x211E, 0x0100);
        WriteMode7Word(ppu, 0x211F, 0x0000);
        WriteMode7Word(ppu, 0x2120, 0x0000);
        WriteMode7Word(ppu, 0x210D, 0x0000);
        WriteMode7Word(ppu, 0x210E, 0x0000);

        ppu.RenderScanline(0);

        Assert.Equal(0xFF730000u, ppu.FrameBuffer[0]);
    }

    [Fact]
    public void OamAddressRegistersSelectWords()
    {
        var ppu = new SnesPpu();
        ppu.WriteRegister(0x2102, 1);
        ppu.WriteRegister(0x2103, 0);
        ppu.WriteRegister(0x2104, 0xAA);
        ppu.WriteRegister(0x2104, 0xBB);

        ppu.WriteRegister(0x2102, 0);
        ppu.WriteRegister(0x2103, 0);
        Assert.Equal(0, ppu.ReadRegister(0x2138));
        Assert.Equal(0, ppu.ReadRegister(0x2138));

        ppu.WriteRegister(0x2102, 1);
        ppu.WriteRegister(0x2103, 0);
        Assert.Equal(0xAA, ppu.ReadRegister(0x2138));
        Assert.Equal(0xBB, ppu.ReadRegister(0x2138));
    }

    [Fact]
    public void OamLowTableCommitsOnlyCompleteWords()
    {
        var ppu = new SnesPpu();
        WriteOamWord(ppu, 1, 0xBBAA);

        ppu.WriteRegister(0x2102, 1);
        ppu.WriteRegister(0x2103, 0);
        ppu.WriteRegister(0x2104, 0xCC);

        ppu.WriteRegister(0x2102, 1);
        ppu.WriteRegister(0x2103, 0);
        Assert.Equal(0xAA, ppu.ReadRegister(0x2138));
        Assert.Equal(0xBB, ppu.ReadRegister(0x2138));

        WriteOamWord(ppu, 1, 0xDDCC);
        ppu.WriteRegister(0x2102, 1);
        ppu.WriteRegister(0x2103, 0);
        Assert.Equal(0xCC, ppu.ReadRegister(0x2138));
        Assert.Equal(0xDD, ppu.ReadRegister(0x2138));
    }

    [Fact]
    public void OamHighTableWritesImmediatelyAndMirrorsEveryThirtyTwoBytes()
    {
        var ppu = new SnesPpu();
        ppu.WriteRegister(0x2102, 0x00);
        ppu.WriteRegister(0x2103, 0x01);
        ppu.WriteRegister(0x2104, 0x5A);

        ppu.WriteRegister(0x2102, 0x10);
        ppu.WriteRegister(0x2103, 0x01);
        ppu.WriteRegister(0x2104, 0x6B);

        ppu.WriteRegister(0x2102, 0x00);
        ppu.WriteRegister(0x2103, 0x01);
        Assert.Equal(0x6B, ppu.ReadRegister(0x2138));
    }

    [Fact]
    public void OamPriorityRotationChangesTheTopmostEqualPrioritySprite()
    {
        var ppu = new SnesPpu();
        SetDisplayOn(ppu);
        WriteColor(ppu, 129, 0x001F);
        WriteColor(ppu, 145, 0x03E0);
        WriteSolidFourBitTile(ppu, 0, 0, 1);
        WriteSolidFourBitTile(ppu, 0, 1, 1);
        FillOamWithOffscreenSprites(ppu);
        WriteOamSprite(ppu, 0, x: 0, y: 0, character: 0, attributes: 0);
        WriteOamSprite(ppu, 1, x: 0, y: 0, character: 1, attributes: 2);
        ppu.WriteRegister(0x212C, 0x10);

        ppu.RenderScanline(0);
        Assert.Equal(0xFFFF0000u, ppu.FrameBuffer[0]);

        // Sprite one begins at OAM word address two.
        ppu.WriteRegister(0x2102, 2);
        ppu.WriteRegister(0x2103, 0x80);
        ppu.RenderScanline(0);
        Assert.Equal(0xFF00FF00u, ppu.FrameBuffer[0]);
    }

    [Fact]
    public void EnabledDisplayRestoresOamAddressAtVBlank()
    {
        var ppu = new SnesPpu();
        SetDisplayOn(ppu);
        WriteOamWord(ppu, 3, 0xBBAA);

        ppu.BeginVBlank();

        Assert.Equal(0xAA, ppu.ReadRegister(0x2138));
        Assert.Equal(0xBB, ppu.ReadRegister(0x2138));
    }

    [Fact]
    public void SpriteStatusReportsThirtyThirdObjectAndThirtyFifthSliver()
    {
        var rangePpu = new SnesPpu();
        SetDisplayOn(rangePpu);
        FillOamWithOffscreenSprites(rangePpu);
        for (var sprite = 0; sprite < 33; sprite++)
        {
            WriteOamSprite(rangePpu, sprite, 0, 0, 0, 0);
        }
        rangePpu.WriteRegister(0x212C, 0x10);

        rangePpu.RenderScanline(0);

        Assert.Equal(0x40, rangePpu.ReadRegister(0x213E) & 0xC0);

        var timePpu = new SnesPpu();
        SetDisplayOn(timePpu);
        FillOamWithOffscreenSprites(timePpu);
        timePpu.WriteRegister(0x2101, 3 << 5);
        for (var sprite = 0; sprite < 9; sprite++)
        {
            WriteOamSprite(timePpu, sprite, 0, 0, 0, 0);
        }
        // Mark the first nine sprites as large (32x32): four slivers each.
        timePpu.WriteRegister(0x2102, 0);
        timePpu.WriteRegister(0x2103, 1);
        timePpu.WriteRegister(0x2104, 0xAA);
        timePpu.WriteRegister(0x2104, 0xAA);
        timePpu.WriteRegister(0x2104, 0x02);
        timePpu.WriteRegister(0x212C, 0x10);

        timePpu.RenderScanline(0);

        Assert.Equal(0x80, timePpu.ReadRegister(0x213E) & 0xC0);
    }

    [Fact]
    public void RectangularObjectModesUseTheirDocumentedHeight()
    {
        var ppu = new SnesPpu();
        SetDisplayOn(ppu);
        WriteColor(ppu, 129, 0x001F);
        WriteSolidFourBitTile(ppu, 0, 48, 1);
        FillOamWithOffscreenSprites(ppu);
        WriteOamSprite(ppu, 0, 0, 0, 0, 0);
        // Object size mode six selects 16x32 for a small sprite.
        ppu.WriteRegister(0x2101, 6 << 5);
        ppu.WriteRegister(0x212C, 0x10);

        ppu.RenderScanline(31);

        Assert.Equal(0xFFFF0000u, ppu.FrameBuffer[31 * SnesPpu.Width]);
    }

    private static void SetDisplayOn(SnesPpu ppu) => ppu.WriteRegister(0x2100, 0x0F);

    private static void WriteColor(SnesPpu ppu, byte index, ushort color)
    {
        ppu.WriteRegister(0x2121, index);
        ppu.WriteRegister(0x2122, (byte)color);
        ppu.WriteRegister(0x2122, (byte)(color >> 8));
    }

    private static void WriteVramWord(SnesPpu ppu, ushort address, ushort value)
    {
        ppu.WriteRegister(0x2115, 0x80);
        ppu.WriteRegister(0x2116, (byte)address);
        ppu.WriteRegister(0x2117, (byte)(address >> 8));
        ppu.WriteRegister(0x2118, (byte)value);
        ppu.WriteRegister(0x2119, (byte)(value >> 8));
    }

    private static void WriteSolidFourBitTile(
        SnesPpu ppu,
        ushort tileBase,
        int character,
        int color)
    {
        var tileAddress = tileBase + (character * 16);
        var planeZero = (color & 1) != 0 ? (byte)0xFF : (byte)0;
        var planeOne = (color & 2) != 0 ? (byte)0xFF : (byte)0;
        var planeTwo = (color & 4) != 0 ? (byte)0xFF : (byte)0;
        var planeThree = (color & 8) != 0 ? (byte)0xFF : (byte)0;
        for (var row = 0; row < 8; row++)
        {
            WriteVramWord(
                ppu,
                (ushort)(tileAddress + row),
                (ushort)(planeZero | (planeOne << 8)));
            WriteVramWord(
                ppu,
                (ushort)(tileAddress + 8 + row),
                (ushort)(planeTwo | (planeThree << 8)));
        }
    }

    private static void WriteMode7Word(SnesPpu ppu, ushort register, short value)
    {
        ppu.WriteRegister(register, (byte)value);
        ppu.WriteRegister(register, (byte)(value >> 8));
    }

    private static void WriteOamWord(SnesPpu ppu, ushort wordAddress, ushort value)
    {
        ppu.WriteRegister(0x2102, (byte)wordAddress);
        ppu.WriteRegister(0x2103, (byte)(wordAddress >> 8));
        ppu.WriteRegister(0x2104, (byte)value);
        ppu.WriteRegister(0x2104, (byte)(value >> 8));
    }

    private static void FillOamWithOffscreenSprites(SnesPpu ppu)
    {
        ppu.WriteRegister(0x2102, 0);
        ppu.WriteRegister(0x2103, 0);
        for (var sprite = 0; sprite < 128; sprite++)
        {
            ppu.WriteRegister(0x2104, 0);
            ppu.WriteRegister(0x2104, 128);
            ppu.WriteRegister(0x2104, 0);
            ppu.WriteRegister(0x2104, 0);
        }

        for (var index = 0; index < 32; index++)
        {
            ppu.WriteRegister(0x2104, 0);
        }
    }

    private static void WriteOamSprite(
        SnesPpu ppu,
        int sprite,
        byte x,
        byte y,
        byte character,
        byte attributes)
    {
        ppu.WriteRegister(0x2102, (byte)(sprite * 2));
        ppu.WriteRegister(0x2103, 0);
        ppu.WriteRegister(0x2104, x);
        ppu.WriteRegister(0x2104, y);
        ppu.WriteRegister(0x2104, character);
        ppu.WriteRegister(0x2104, attributes);
    }
}
