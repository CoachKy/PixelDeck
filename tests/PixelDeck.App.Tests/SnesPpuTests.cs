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
        WriteVramWord(ppu, 0x1000, 0x0080);
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

    private static void WriteMode7Word(SnesPpu ppu, ushort register, short value)
    {
        ppu.WriteRegister(register, (byte)value);
        ppu.WriteRegister(register, (byte)(value >> 8));
    }
}
