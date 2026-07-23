using PixelDeck.Emulation.Nes;

namespace PixelDeck.App.Tests;

public sealed class NesPpuTests
{
    [Fact]
    public void RemoveSpriteLimitRendersTheNinthSpriteWithoutHidingOverflow()
    {
        using var image = SolidSpriteNesImage.Create();

        var accurate = RenderNineSprites(image.Path, removeSpriteLimit: false);
        var enhanced = RenderNineSprites(image.Path, removeSpriteLimit: true);

        Assert.Equal(0xFF666666u, accurate.NinthSpriteFirstPixel);
        Assert.Equal(0xFF666666u, accurate.NinthSpriteLastPixel);
        Assert.Equal(0xFF64B0FFu, enhanced.NinthSpriteFirstPixel);
        Assert.Equal(0xFF64B0FFu, enhanced.NinthSpriteLastPixel);
        Assert.True(enhanced.SpriteOverflow);
    }

    [Fact]
    public void SpriteOverflowIsRaisedOnTheNinthSpritesEvenEvaluationDot()
    {
        using var image = SolidSpriteNesImage.Create();
        var ppu = new NesPpu(Cartridge.Load(image.Path));
        WriteSprites(
            ppu,
            sprite => sprite < 9
                ? ((byte)0, (byte)1, (byte)0, (byte)(sprite * 8))
                : ((byte)0xF0, (byte)0, (byte)0, (byte)0));
        ppu.CpuWriteRegister(1, 0x18);

        AdvanceTo(ppu, scanline: 0, cycle: 130);

        Assert.Equal(0, ppu.CpuReadRegister(2) & 0x20);
        ppu.Tick();
        Assert.Equal(0x20, ppu.CpuReadRegister(2) & 0x20);
    }

    [Fact]
    public void SpriteOverflowDiagonalBugCanTreatATileByteAsY()
    {
        using var image = SolidSpriteNesImage.Create();
        var ppu = new NesPpu(Cartridge.Load(image.Path));
        WriteSprites(
            ppu,
            sprite => sprite switch
            {
                < 8 => ((byte)0, (byte)1, (byte)0, (byte)(sprite * 8)),
                9 => ((byte)0xF0, (byte)0, (byte)0xF0, (byte)0xF0),
                _ => ((byte)0xF0, (byte)0xF0, (byte)0xF0, (byte)0xF0)
            });
        ppu.CpuWriteRegister(1, 0x18);

        AdvanceTo(ppu, scanline: 0, cycle: 132);

        Assert.Equal(0, ppu.CpuReadRegister(2) & 0x20);
        ppu.Tick();
        Assert.Equal(0x20, ppu.CpuReadRegister(2) & 0x20);
    }

    [Fact]
    public void SpriteOverflowDiagonalSearchStopsWithoutAnInRangeByte()
    {
        using var image = SolidSpriteNesImage.Create();
        var ppu = new NesPpu(Cartridge.Load(image.Path));
        WriteSprites(
            ppu,
            sprite => sprite < 8
                ? ((byte)0, (byte)1, (byte)0, (byte)(sprite * 8))
                : ((byte)0xF0, (byte)0xF0, (byte)0xF0, (byte)0xF0));
        ppu.CpuWriteRegister(1, 0x18);

        AdvanceTo(ppu, scanline: 1, cycle: 0);

        Assert.Equal(0, ppu.CpuReadRegister(2) & 0x20);
    }

    [Fact]
    public void SaveStatePreservesAnInFlightDiagonalOverflowEvaluation()
    {
        using var image = SolidSpriteNesImage.Create();
        var original = new NesPpu(Cartridge.Load(image.Path));
        WriteSprites(
            original,
            sprite => sprite switch
            {
                < 8 => ((byte)0, (byte)1, (byte)0, (byte)(sprite * 8)),
                9 => ((byte)0xF0, (byte)0, (byte)0xF0, (byte)0xF0),
                _ => ((byte)0xF0, (byte)0xF0, (byte)0xF0, (byte)0xF0)
            });
        original.CpuWriteRegister(1, 0x18);
        AdvanceTo(original, scanline: 0, cycle: 131);

        byte[] state;
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                original.SaveState(writer);
            }

            state = stream.ToArray();
        }

        var restored = new NesPpu(Cartridge.Load(image.Path));
        using (var stream = new MemoryStream(state, writable: false))
        using (var reader = new BinaryReader(stream))
        {
            restored.LoadState(reader);
        }

        original.Tick();
        restored.Tick();
        Assert.Equal(original.Cycle, restored.Cycle);
        Assert.Equal(original.CpuReadRegister(2), restored.CpuReadRegister(2));

        original.Tick();
        restored.Tick();
        Assert.Equal(0x20, original.CpuReadRegister(2) & 0x20);
        Assert.Equal(original.CpuReadRegister(2), restored.CpuReadRegister(2));
    }

    [Fact]
    public void FrameBufferPixelsAreProducedOnTheirIndividualPpuDots()
    {
        using var image = SolidSpriteNesImage.Create();
        var ppu = new NesPpu(Cartridge.Load(image.Path));
        ppu.CpuWriteRegister(6, 0x3F);
        ppu.CpuWriteRegister(6, 0x00);
        ppu.CpuWriteRegister(7, 0x01);

        while (ppu.Scanline != 0 || ppu.Cycle != 1)
        {
            ppu.Tick();
        }

        Assert.Equal(0u, ppu.FrameBuffer[0]);
        Assert.Equal(0u, ppu.FrameBuffer[1]);

        ppu.Tick();

        Assert.Equal(0xFF002A88u, ppu.FrameBuffer[0]);
        Assert.Equal(0u, ppu.FrameBuffer[1]);

        ppu.Tick();

        Assert.Equal(0xFF002A88u, ppu.FrameBuffer[1]);
    }

    private static void AdvanceTo(NesPpu ppu, int scanline, int cycle)
    {
        for (var ticks = 0; ticks < 100_000; ticks++)
        {
            if (ppu.Scanline == scanline && ppu.Cycle == cycle)
            {
                return;
            }

            ppu.Tick();
        }

        throw new TimeoutException($"PPU did not reach scanline {scanline}, cycle {cycle}.");
    }

    private static void WriteSprites(
        NesPpu ppu,
        Func<int, (byte Y, byte Tile, byte Attributes, byte X)> createSprite)
    {
        ppu.CpuWriteRegister(3, 0);
        for (var sprite = 0; sprite < 64; sprite++)
        {
            var data = createSprite(sprite);
            ppu.CpuWriteRegister(4, data.Y);
            ppu.CpuWriteRegister(4, data.Tile);
            ppu.CpuWriteRegister(4, data.Attributes);
            ppu.CpuWriteRegister(4, data.X);
        }
    }

    [Fact]
    public void BackgroundFetchPipelineLoadsTilePatternAndAttributeShifters()
    {
        using var image = SolidSpriteNesImage.Create();
        var ppu = new NesPpu(Cartridge.Load(image.Path));
        ppu.CpuWriteRegister(6, 0x20);
        ppu.CpuWriteRegister(6, 0x00);
        ppu.CpuWriteRegister(7, 0x01);
        ppu.CpuWriteRegister(6, 0x3F);
        ppu.CpuWriteRegister(6, 0x01);
        ppu.CpuWriteRegister(7, 0x21);
        ppu.CpuWriteRegister(5, 0);
        ppu.CpuWriteRegister(5, 0);
        ppu.CpuWriteRegister(0, 0);
        ppu.CpuWriteRegister(1, 0x0A);

        while (ppu.Scanline != 0 || ppu.Cycle != 9)
        {
            ppu.Tick();
        }

        Assert.All(
            ppu.FrameBuffer.AsSpan(0, 8).ToArray(),
            pixel => Assert.Equal(0xFF64B0FFu, pixel));
    }

    [Fact]
    public void OpenBusDecaysAndPaletteReadsPreserveItsUpperBits()
    {
        using var image = SolidSpriteNesImage.Create();
        var ppu = new NesPpu(Cartridge.Load(image.Path));

        ppu.CpuWriteRegister(2, 0xFF);
        Assert.Equal(0xFF, ppu.CpuReadRegister(0));
        for (var tick = 0; tick < 5_000_001; tick++)
        {
            ppu.Tick();
        }
        Assert.Equal(0, ppu.CpuReadRegister(0));

        ppu.CpuWriteRegister(6, 0x3F);
        ppu.CpuWriteRegister(6, 0x00);
        ppu.CpuWriteRegister(7, 0x01);
        ppu.CpuWriteRegister(6, 0x3F);
        ppu.CpuWriteRegister(6, 0x00);
        ppu.CpuWriteRegister(2, 0xC0);

        Assert.Equal(0xC1, ppu.CpuReadRegister(7));
    }

    [Fact]
    public void ReadingSpriteAttributesGroundsUnusedBits()
    {
        using var image = SolidSpriteNesImage.Create();
        var ppu = new NesPpu(Cartridge.Load(image.Path));
        ppu.CpuWriteRegister(3, 2);
        ppu.CpuWriteRegister(4, 0xFF);
        ppu.CpuWriteRegister(3, 2);

        Assert.Equal(0xE3, ppu.CpuReadRegister(4));
    }

    [Fact]
    public void RenderingOamDataReadsFollowTheClearEvaluationAndFetchLatches()
    {
        using var image = SolidSpriteNesImage.Create();
        var ppu = new NesPpu(Cartridge.Load(image.Path));
        WriteSprites(
            ppu,
            sprite => sprite == 0
                ? ((byte)0, (byte)1, (byte)0xFF, (byte)3)
                : ((byte)0xF0, (byte)0, (byte)0, (byte)0));
        ppu.CpuWriteRegister(1, 0x18);

        AdvanceTo(ppu, scanline: 0, cycle: 10);
        Assert.Equal(0xFF, ppu.CpuReadRegister(4));

        AdvanceTo(ppu, scanline: 0, cycle: 65);
        ppu.Tick();
        Assert.Equal(0, ppu.CpuReadRegister(4));

        AdvanceTo(ppu, scanline: 0, cycle: 257);
        Assert.Equal(0, ppu.CpuReadRegister(4));
        ppu.Tick();
        Assert.Equal(1, ppu.CpuReadRegister(4));
        ppu.Tick();
        Assert.Equal(0xE3, ppu.CpuReadRegister(4));
        ppu.Tick();
        Assert.Equal(3, ppu.CpuReadRegister(4));
        ppu.Tick();
        Assert.Equal(3, ppu.CpuReadRegister(4));
    }

    [Fact]
    public void RenderingOamDataWritesOnlyAdvanceTheSpriteIndexBits()
    {
        using var image = SolidSpriteNesImage.Create();
        var ppu = new NesPpu(Cartridge.Load(image.Path));
        WriteOamByte(ppu, 0, 0x11);
        WriteOamByte(ppu, 4, 0x44);
        ppu.CpuWriteRegister(3, 0);
        ppu.CpuWriteRegister(1, 0x18);
        ppu.Tick();

        ppu.CpuWriteRegister(4, 0xAA);
        ppu.CpuWriteRegister(1, 0);
        ppu.Tick();

        Assert.Equal(0x44, ppu.CpuReadRegister(4));
        ppu.CpuWriteRegister(3, 0);
        Assert.Equal(0x11, ppu.CpuReadRegister(4));
    }

    [Fact]
    public void PreRenderOamAddressCopiesTheSelectedRowOverRowZero()
    {
        using var image = SolidSpriteNesImage.Create();
        var ppu = new NesPpu(Cartridge.Load(image.Path));
        var expected = new byte[8];
        for (var index = 0; index < 8; index++)
        {
            WriteOamByte(ppu, index, (byte)(0x10 + index));
            expected[index] = MaskOamAttributeByte(index, (byte)(0x80 + index));
            WriteOamByte(ppu, 8 + index, expected[index]);
        }

        ppu.CpuWriteRegister(3, 8);
        ppu.CpuWriteRegister(1, 0x18);
        AdvanceTo(ppu, scanline: 261, cycle: 9);
        ppu.CpuWriteRegister(1, 0);
        ppu.Tick();

        for (var index = 0; index < expected.Length; index++)
        {
            ppu.CpuWriteRegister(3, (byte)index);
            Assert.Equal(expected[index], ppu.CpuReadRegister(4));
        }
    }

    [Theory]
    [InlineData(20, 10)]
    [InlineData(260, 3)]
    public void RenderingToggleCorruptionCopiesRowZeroIntoTheAffectedRow(
        int disableCycle,
        int affectedRow)
    {
        using var image = SolidSpriteNesImage.Create();
        var ppu = new NesPpu(Cartridge.Load(image.Path));
        var expected = FillOamRowsForCorruption(ppu, affectedRow);
        ppu.CpuWriteRegister(3, 0);
        ppu.CpuWriteRegister(1, 0x18);
        AdvanceTo(ppu, scanline: 0, cycle: disableCycle);

        ppu.CpuWriteRegister(1, 0);
        ppu.Tick();
        ppu.CpuWriteRegister(1, 0x18);
        ppu.Tick();
        ppu.CpuWriteRegister(1, 0);
        ppu.Tick();

        for (var index = 0; index < expected.Length; index++)
        {
            ppu.CpuWriteRegister(3, (byte)((affectedRow * 8) + index));
            Assert.Equal(expected[index], ppu.CpuReadRegister(4));
        }
    }

    [Fact]
    public void SaveStatePreservesPendingRenderingToggleOamCorruption()
    {
        using var image = SolidSpriteNesImage.Create();
        var original = new NesPpu(Cartridge.Load(image.Path));
        var expected = FillOamRowsForCorruption(original, affectedRow: 10);
        original.CpuWriteRegister(3, 0);
        original.CpuWriteRegister(1, 0x18);
        AdvanceTo(original, scanline: 0, cycle: 20);
        original.CpuWriteRegister(1, 0);
        original.Tick();

        byte[] state;
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                original.SaveState(writer);
            }

            state = stream.ToArray();
        }

        var restored = new NesPpu(Cartridge.Load(image.Path));
        using (var stream = new MemoryStream(state, writable: false))
        using (var reader = new BinaryReader(stream))
        {
            restored.LoadState(reader);
        }

        restored.CpuWriteRegister(1, 0x18);
        restored.Tick();
        restored.CpuWriteRegister(1, 0);
        restored.Tick();
        for (var index = 0; index < expected.Length; index++)
        {
            restored.CpuWriteRegister(3, (byte)(80 + index));
            Assert.Equal(expected[index], restored.CpuReadRegister(4));
        }
    }

    [Fact]
    public void OamDecaySettlesOnlyAnUnrefreshedEightByteRow()
    {
        using var image = SolidSpriteNesImage.Create();
        var ppu = new NesPpu(
            Cartridge.Load(image.Path),
            new NesEmulationOptions { EnableOamDecay = true });
        for (var index = 0; index < 8; index++)
        {
            WriteOamByte(ppu, 16 + index, (byte)(0xA0 + index));
        }

        // Forced blank continuously refreshes only the OAMADDR-selected row.
        ppu.CpuWriteRegister(3, 0);
        for (var tick = 0; tick < 9_001; tick++)
        {
            ppu.Tick();
        }

        for (var index = 0; index < 8; index++)
        {
            var address = 16 + index;
            ppu.CpuWriteRegister(3, (byte)address);
            Assert.Equal(
                MaskOamAttributeByte(address, (byte)address),
                ppu.CpuReadRegister(4));
        }

        ppu.CpuWriteRegister(3, 0);
        Assert.Equal(0, ppu.CpuReadRegister(4));
    }

    [Fact]
    public void AnyOamByteReadRefreshesItsEntireElectricalRow()
    {
        using var image = SolidSpriteNesImage.Create();
        var ppu = new NesPpu(
            Cartridge.Load(image.Path),
            new NesEmulationOptions { EnableOamDecay = true });
        var expected = new byte[8];
        for (var index = 0; index < expected.Length; index++)
        {
            expected[index] = MaskOamAttributeByte(40 + index, (byte)(0x90 + index));
            WriteOamByte(ppu, 40 + index, expected[index]);
        }

        ppu.CpuWriteRegister(3, 0);
        for (var tick = 0; tick < 8_000; tick++) ppu.Tick();
        ppu.CpuWriteRegister(3, 40);
        Assert.Equal(expected[0], ppu.CpuReadRegister(4));

        ppu.CpuWriteRegister(3, 0);
        for (var tick = 0; tick < 8_000; tick++) ppu.Tick();
        for (var index = 0; index < expected.Length; index++)
        {
            ppu.CpuWriteRegister(3, (byte)(40 + index));
            Assert.Equal(expected[index], ppu.CpuReadRegister(4));
        }
    }

    [Fact]
    public void SaveStatePreservesOamRowChargeAge()
    {
        using var image = SolidSpriteNesImage.Create();
        var options = new NesEmulationOptions { EnableOamDecay = true };
        var original = new NesPpu(Cartridge.Load(image.Path), options);
        WriteOamByte(original, 24, 0xAA);
        original.CpuWriteRegister(3, 0);
        for (var tick = 0; tick < 8_500; tick++) original.Tick();

        byte[] state;
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                original.SaveState(writer);
            }

            state = stream.ToArray();
        }

        var restored = new NesPpu(Cartridge.Load(image.Path), options);
        using (var stream = new MemoryStream(state, writable: false))
        using (var reader = new BinaryReader(stream))
        {
            restored.LoadState(reader);
        }

        for (var tick = 0; tick < 501; tick++) restored.Tick();
        restored.CpuWriteRegister(3, 24);
        Assert.Equal(24, restored.CpuReadRegister(4));
    }

    [Fact]
    public void EarlyPpuCanCreateThePostWrapSpriteAtTheRightEdge()
    {
        using var image = SolidSpriteNesImage.Create();

        var modernPixel = RenderEarlyPpuPostWrapPixel(
            image.Path,
            NesPpuRevision.Rp2C02G);
        var earlyPixel = RenderEarlyPpuPostWrapPixel(
            image.Path,
            NesPpuRevision.Rp2C02BOrEarlier);

        Assert.Equal(0xFF666666u, modernPixel);
        Assert.Equal(0xFF64B0FFu, earlyPixel);
    }

    [Fact]
    public void EarlyPpuDoesNotHaveTheLaterPreRenderRowCopyBug()
    {
        using var image = SolidSpriteNesImage.Create();
        var ppu = new NesPpu(
            Cartridge.Load(image.Path),
            new NesEmulationOptions { PpuRevision = NesPpuRevision.Rp2C02BOrEarlier });
        var rowZero = new byte[8];
        for (var index = 0; index < 8; index++)
        {
            rowZero[index] = MaskOamAttributeByte(index, (byte)(0x10 + index));
            WriteOamByte(ppu, index, rowZero[index]);
            WriteOamByte(ppu, 8 + index, (byte)(0x80 + index));
        }

        ppu.CpuWriteRegister(3, 8);
        ppu.CpuWriteRegister(1, 0x18);
        AdvanceTo(ppu, scanline: 261, cycle: 9);
        ppu.CpuWriteRegister(1, 0);
        ppu.Tick();

        for (var index = 0; index < rowZero.Length; index++)
        {
            ppu.CpuWriteRegister(3, (byte)index);
            Assert.Equal(rowZero[index], ppu.CpuReadRegister(4));
        }
    }

    private static byte[] FillOamRowsForCorruption(NesPpu ppu, int affectedRow)
    {
        var expected = new byte[8];
        for (var index = 0; index < expected.Length; index++)
        {
            expected[index] = MaskOamAttributeByte(index, (byte)(0x20 + index));
            WriteOamByte(ppu, index, expected[index]);
            WriteOamByte(ppu, (affectedRow * 8) + index, (byte)(0xA0 + index));
        }

        return expected;
    }

    private static byte MaskOamAttributeByte(int address, byte value) =>
        (address & 0x03) == 0x02 ? (byte)(value & 0xE3) : value;

    private static void WriteOamByte(NesPpu ppu, int address, byte value)
    {
        ppu.CpuWriteRegister(3, (byte)address);
        ppu.CpuWriteRegister(4, value);
    }

    private static uint RenderEarlyPpuPostWrapPixel(string path, NesPpuRevision revision)
    {
        var ppu = new NesPpu(
            Cartridge.Load(path),
            new NesEmulationOptions { PpuRevision = revision });
        WriteSprites(
            ppu,
            sprite => sprite == 29
                ? ((byte)0, (byte)0, (byte)0, (byte)0)
                : ((byte)0xF0, (byte)0, (byte)0, (byte)0));

        ppu.CpuWriteRegister(6, 0x3F);
        ppu.CpuWriteRegister(6, 0x1D);
        ppu.CpuWriteRegister(7, 0x21);
        ppu.CpuWriteRegister(1, 0x14);
        AdvanceTo(ppu, scanline: 0, cycle: 0);

        // Starting at sprite 62 makes primary OAM wrap immediately. On early
        // PPUs the final post-wrap Y byte comes from sprite 29 at dot 255.
        ppu.CpuWriteRegister(3, 0xF8);
        AdvanceTo(ppu, scanline: 2, cycle: 0);
        return ppu.FrameBuffer[(1 * NesPpu.Width) + 255];
    }

    private static (uint NinthSpriteFirstPixel, uint NinthSpriteLastPixel, bool SpriteOverflow) RenderNineSprites(
        string path,
        bool removeSpriteLimit)
    {
        var cartridge = Cartridge.Load(path);
        var ppu = new NesPpu(
            cartridge,
            new NesEmulationOptions { RemoveSpriteLimit = removeSpriteLimit });

        ppu.CpuWriteRegister(6, 0x3F);
        ppu.CpuWriteRegister(6, 0x11);
        ppu.CpuWriteRegister(7, 0x21);

        ppu.CpuWriteRegister(3, 0);
        for (var sprite = 0; sprite < 64; sprite++)
        {
            var visible = sprite < 9;
            ppu.CpuWriteRegister(4, visible ? (byte)0 : (byte)0xF0);
            ppu.CpuWriteRegister(4, visible ? (byte)1 : (byte)0);
            ppu.CpuWriteRegister(4, 0);
            ppu.CpuWriteRegister(4, visible ? (byte)(sprite * 8) : (byte)0);
        }

        ppu.CpuWriteRegister(1, 0x14);
        for (var ticks = 0; ticks < 1_000 && (ppu.Scanline != 1 || ppu.Cycle <= 72); ticks++)
        {
            ppu.Tick();
        }

        var status = ppu.CpuReadRegister(2);
        return (ppu.FrameBuffer[256 + 64], ppu.FrameBuffer[256 + 71], (status & 0x20) != 0);
    }

    private sealed class SolidSpriteNesImage : IDisposable
    {
        private SolidSpriteNesImage(string directoryPath, string path)
        {
            DirectoryPath = directoryPath;
            Path = path;
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public static SolidSpriteNesImage Create()
        {
            var directoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PixelDeck.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var path = System.IO.Path.Combine(directoryPath, "solid-sprite.nes");
            var image = new byte[16 + 16_384 + 8_192];
            image[0] = (byte)'N';
            image[1] = (byte)'E';
            image[2] = (byte)'S';
            image[3] = 0x1A;
            image[4] = 1;
            image[5] = 1;

            var chrOffset = 16 + 16_384;
            for (var row = 0; row < 8; row++)
            {
                image[chrOffset + 16 + row] = 0xFF;
                image[chrOffset + (255 * 16) + row] = 0xFF;
            }

            File.WriteAllBytes(path, image);
            return new SolidSpriteNesImage(directoryPath, path);
        }

        public void Dispose()
        {
            var testRoot = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PixelDeck.Tests"))
                .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var resolvedDirectory = System.IO.Path.GetFullPath(DirectoryPath);
            if (!resolvedDirectory.StartsWith(testRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to remove a directory outside the PixelDeck test area.");
            }

            if (Directory.Exists(resolvedDirectory))
            {
                Directory.Delete(resolvedDirectory, recursive: true);
            }
        }
    }
}
