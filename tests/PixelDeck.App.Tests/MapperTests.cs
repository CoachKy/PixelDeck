using PixelDeck.Emulation.Nes;

namespace PixelDeck.App.Tests;

public sealed class MapperTests
{
    [Fact]
    public void Mapper0MirrorsSixteenKilobytePrgAndKeepsChrRomReadOnly()
    {
        using var image = TemporaryNesImage.Create(mapper: 0, submapper: 0, prgBanks: 1, chrBanks: 1);
        var bytes = File.ReadAllBytes(image.Path);
        bytes[16] = 0x42;
        bytes[16 + 16_384 + 0x123] = 0x7B;
        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        Assert.Equal(0x42, cartridge.CpuRead(0x8000));
        Assert.Equal(0x42, cartridge.CpuRead(0xC000));
        Assert.Equal(0x7B, cartridge.PpuRead(0x0123));

        cartridge.PpuWrite(0x0123, 0x55);

        Assert.Equal(0x7B, cartridge.PpuRead(0x0123));
    }

    [Fact]
    public void Mapper1SerialWritesSelectPrgBankAndMirroringAndRestoreState()
    {
        using var image = TemporaryNesImage.Create(mapper: 1, submapper: 0, prgBanks: 4, chrBanks: 1);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 4; bank++)
        {
            Array.Fill(bytes, (byte)(0x20 + bank), 16 + (bank * 16_384), 16_384);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        WriteMmc1Register(cartridge, 0x8000, 0x0E);
        WriteMmc1Register(cartridge, 0xE000, 0x02);
        Assert.Equal(NametableMirroring.Vertical, cartridge.Mirroring);
        Assert.Equal(0x22, cartridge.CpuRead(0x8000));
        Assert.Equal(0x23, cartridge.CpuRead(0xC000));

        var state = SaveCartridgeState(cartridge);
        WriteMmc1Register(cartridge, 0xE000, 0x01);
        Assert.Equal(0x21, cartridge.CpuRead(0x8000));
        LoadCartridgeState(cartridge, state);

        Assert.Equal(0x22, cartridge.CpuRead(0x8000));
        Assert.Equal(NametableMirroring.Vertical, cartridge.Mirroring);
    }

    [Fact]
    public void Mapper2SwitchesTheLowerPrgBankAndFixesTheUpperBank()
    {
        using var image = TemporaryNesImage.Create(mapper: 2, submapper: 1, prgBanks: 4, chrBanks: 1);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 4; bank++)
        {
            Array.Fill(bytes, (byte)(0x30 + bank), 16 + (bank * 16_384), 16_384);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        Assert.Equal(0x30, cartridge.CpuRead(0x8000));
        Assert.Equal(0x33, cartridge.CpuRead(0xC000));
        cartridge.CpuWrite(0x8000, 2);

        Assert.Equal(0x32, cartridge.CpuRead(0x8000));
        Assert.Equal(0x33, cartridge.CpuRead(0xC000));
    }

    [Fact]
    public void Mapper3SwitchesChrBanksAndRestoresItsState()
    {
        using var image = TemporaryNesImage.Create(mapper: 3, submapper: 1, prgBanks: 2, chrBanks: 4);
        var bytes = File.ReadAllBytes(image.Path);
        var chrOffset = 16 + (2 * 16_384);
        for (var bank = 0; bank < 4; bank++)
        {
            Array.Fill(bytes, (byte)(0x40 + bank), chrOffset + (bank * 8_192), 8_192);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        Assert.Equal(0x40, cartridge.PpuRead(0));
        cartridge.CpuWrite(0x8000, 2);
        Assert.Equal(0x42, cartridge.PpuRead(0));

        var state = SaveCartridgeState(cartridge);
        cartridge.CpuWrite(0x8000, 1);
        Assert.Equal(0x41, cartridge.PpuRead(0));

        LoadCartridgeState(cartridge, state);
        Assert.Equal(0x42, cartridge.PpuRead(0));
    }

    [Fact]
    public void Mapper3Submapper2AppliesAndTypeBusConflicts()
    {
        using var image = TemporaryNesImage.Create(mapper: 3, submapper: 2, prgBanks: 2, chrBanks: 4);
        var bytes = File.ReadAllBytes(image.Path);
        bytes[16] = 0x01;
        var chrOffset = 16 + (2 * 16_384);
        for (var bank = 0; bank < 4; bank++)
        {
            Array.Fill(bytes, (byte)(0x50 + bank), chrOffset + (bank * 8_192), 8_192);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        cartridge.CpuWrite(0x8000, 3);

        Assert.Equal(0x51, cartridge.PpuRead(0));
    }

    [Fact]
    public void Mapper4SwitchesBanksAndRaisesAndRestoresItsScanlineIrq()
    {
        using var image = TemporaryNesImage.Create(mapper: 4, submapper: 0, prgBanks: 4, chrBanks: 2);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 8; bank++)
        {
            Array.Fill(bytes, (byte)(0x40 + bank), 16 + (bank * 8_192), 8_192);
        }

        var chrOffset = 16 + (4 * 16_384);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(bytes, (byte)(0x80 + bank), chrOffset + (bank * 1_024), 1_024);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        cartridge.CpuWrite(0x8000, 0x06);
        cartridge.CpuWrite(0x8001, 0x03);
        Assert.Equal(0x43, cartridge.CpuRead(0x8000));
        Assert.Equal(0x46, cartridge.CpuRead(0xC000));

        cartridge.CpuWrite(0x8000, 0x00);
        cartridge.CpuWrite(0x8001, 0x04);
        Assert.Equal(0x84, cartridge.PpuRead(0x0000));
        Assert.Equal(0x85, cartridge.PpuRead(0x0400));

        cartridge.CpuWrite(0xC000, 2);
        cartridge.CpuWrite(0xC001, 0);
        cartridge.CpuWrite(0xE001, 0);
        cartridge.ClockScanline();
        cartridge.ClockScanline();
        Assert.False(cartridge.IrqPending);
        var state = SaveCartridgeState(cartridge);

        cartridge.ClockScanline();
        Assert.True(cartridge.IrqPending);
        cartridge.CpuWrite(0xE000, 0);
        Assert.False(cartridge.IrqPending);
        LoadCartridgeState(cartridge, state);
        Assert.False(cartridge.IrqPending);
        cartridge.ClockScanline();
        Assert.True(cartridge.IrqPending);
    }

    [Fact]
    public void Mapper4IrqUsesFilteredPpuA12RisingEdges()
    {
        using var image = TemporaryNesImage.Create(mapper: 4, submapper: 0, prgBanks: 4, chrBanks: 2);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0xC000, 1);
        cartridge.CpuWrite(0xC001, 0);
        cartridge.CpuWrite(0xE001, 0);

        ClockPpuAddress(cartridge, 0x0000, 8);
        cartridge.ClockPpuAddress(0x1000);
        Assert.False(cartridge.IrqPending);

        ClockPpuAddress(cartridge, 0x0000, 7);
        cartridge.ClockPpuAddress(0x1000);
        Assert.False(cartridge.IrqPending);

        ClockPpuAddress(cartridge, 0x0000, 8);
        cartridge.ClockPpuAddress(0x1000);
        Assert.True(cartridge.IrqPending);
    }

    [Fact]
    public void VisibleLineDotZeroSuppressesTheFalseMmc3BackgroundA12Edge()
    {
        using var image = TemporaryNesImage.Create(mapper: 4, submapper: 0, prgBanks: 4, chrBanks: 2);
        var cartridge = Cartridge.Load(image.Path);
        var ppu = new NesPpu(cartridge);
        cartridge.CpuWrite(0xC000, 2);
        cartridge.CpuWrite(0xC001, 0);
        cartridge.CpuWrite(0xE001, 0);
        ppu.CpuWriteRegister(0, 0x10);
        ppu.CpuWriteRegister(1, 0x18);

        ClockPpuAddress(cartridge, 0x0000, 8);
        for (var tick = 0; tick < 1_000 && !cartridge.IrqPending; tick++)
        {
            ppu.Tick();
        }

        Assert.True(cartridge.IrqPending);
        Assert.Equal(0, ppu.Scanline);
        Assert.Equal(326, ppu.Cycle);
    }

    [Fact]
    public void Mapper4SharpRevisionRaisesAnIrqOnEveryZeroLatchClock()
    {
        using var image = TemporaryNesImage.Create(mapper: 4, submapper: 0, prgBanks: 4, chrBanks: 2);
        var cartridge = Cartridge.Load(image.Path);

        AssertZeroLatchIrq(cartridge, expectedAfterFirstClock: true, expectedAfterSecondClock: true);
    }

    [Fact]
    public void Mapper4Nes20SubmapperFourSelectsNecIrqBehavior()
    {
        using var image = TemporaryNesImage.Create(mapper: 4, submapper: 4, prgBanks: 4, chrBanks: 2);
        var info = Cartridge.Inspect(image.Path);
        var cartridge = Cartridge.Load(image.Path);

        Assert.True(info.IsSupported);
        AssertZeroLatchIrq(cartridge, expectedAfterFirstClock: true, expectedAfterSecondClock: false);
    }

    [Fact]
    public void Mapper4IrqRevisionOverrideResolvesAmbiguousHeadersAndProtectsSaveStates()
    {
        using var image = TemporaryNesImage.Create(mapper: 4, submapper: 0, prgBanks: 4, chrBanks: 2);
        var necCartridge = Cartridge.Load(
            image.Path,
            mmc3IrqRevision: Mmc3IrqRevision.Nec);
        AssertZeroLatchIrq(necCartridge, expectedAfterFirstClock: true, expectedAfterSecondClock: false);
        var necState = SaveCartridgeState(necCartridge);

        var sharpCartridge = Cartridge.Load(
            image.Path,
            mmc3IrqRevision: Mmc3IrqRevision.Sharp);

        Assert.Throws<InvalidDataException>(() => LoadCartridgeState(sharpCartridge, necState));
    }

    [Fact]
    public void Mapper7SwitchesPrgBankMirroringAndChrRamAndRestoresItsState()
    {
        using var image = TemporaryNesImage.Create(mapper: 7, submapper: 1, prgBanks: 8, chrBanks: 0);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 4; bank++)
        {
            Array.Fill(bytes, (byte)(0x30 + bank), 16 + (bank * 32_768), 32_768);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        Assert.Equal(0x30, cartridge.CpuRead(0x8000));
        Assert.Equal(NametableMirroring.OneScreenLower, cartridge.Mirroring);

        cartridge.CpuWrite(0x8000, 0x13);
        cartridge.PpuWrite(0x0123, 0xA5);
        Assert.Equal(0x33, cartridge.CpuRead(0x8000));
        Assert.Equal(NametableMirroring.OneScreenUpper, cartridge.Mirroring);
        Assert.Equal(0xA5, cartridge.PpuRead(0x0123));

        var state = SaveCartridgeState(cartridge);
        cartridge.CpuWrite(0x8000, 0x01);
        cartridge.PpuWrite(0x0123, 0x5A);
        LoadCartridgeState(cartridge, state);

        Assert.Equal(0x33, cartridge.CpuRead(0x8000));
        Assert.Equal(NametableMirroring.OneScreenUpper, cartridge.Mirroring);
        Assert.Equal(0xA5, cartridge.PpuRead(0x0123));
    }

    [Fact]
    public void Mapper66AppliesBusConflictsAndSwitchesPrgAndChrTogether()
    {
        using var image = TemporaryNesImage.Create(mapper: 66, submapper: 0, prgBanks: 8, chrBanks: 4);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 4; bank++)
        {
            Array.Fill(bytes, (byte)(0x60 + bank), 16 + (bank * 32_768), 32_768);
        }

        bytes[16] = 0x21;
        var chrOffset = 16 + (8 * 16_384);
        for (var bank = 0; bank < 4; bank++)
        {
            Array.Fill(bytes, (byte)(0x70 + bank), chrOffset + (bank * 8_192), 8_192);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        cartridge.CpuWrite(0x8000, 0xFF);

        Assert.Equal(0x62, cartridge.CpuRead(0x8001));
        Assert.Equal(0x71, cartridge.PpuRead(0));
    }

    [Fact]
    public void InspectionReportsUnsupportedSubmapperVariants()
    {
        using var image = TemporaryNesImage.Create(mapper: 3, submapper: 3, prgBanks: 2, chrBanks: 1);

        var info = Cartridge.Inspect(image.Path);

        Assert.Equal(3, info.MapperNumber);
        Assert.Equal(3, info.SubmapperNumber);
        Assert.False(info.IsSupported);
        Assert.Throws<NotSupportedException>(() => Cartridge.Load(image.Path));
    }

    private static byte[] SaveCartridgeState(Cartridge cartridge)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        cartridge.SaveState(writer);
        writer.Flush();
        return stream.ToArray();
    }

    private static void LoadCartridgeState(Cartridge cartridge, byte[] state)
    {
        using var stream = new MemoryStream(state, writable: false);
        using var reader = new BinaryReader(stream);
        cartridge.LoadState(reader);
    }

    private static void WriteMmc1Register(Cartridge cartridge, ushort address, byte value)
    {
        for (var bit = 0; bit < 5; bit++)
        {
            cartridge.CpuWrite(address, (byte)((value >> bit) & 1));
        }
    }

    private static void ClockPpuAddress(Cartridge cartridge, ushort address, int cycles)
    {
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            cartridge.ClockPpuAddress(address);
        }
    }

    private static void AssertZeroLatchIrq(
        Cartridge cartridge,
        bool expectedAfterFirstClock,
        bool expectedAfterSecondClock)
    {
        cartridge.CpuWrite(0xC000, 0);
        cartridge.CpuWrite(0xC001, 0);
        cartridge.CpuWrite(0xE001, 0);
        ClockPpuAddress(cartridge, 0x0000, 8);
        cartridge.ClockPpuAddress(0x1000);
        Assert.Equal(expectedAfterFirstClock, cartridge.IrqPending);

        cartridge.CpuWrite(0xE000, 0);
        cartridge.CpuWrite(0xE001, 0);
        ClockPpuAddress(cartridge, 0x0000, 8);
        cartridge.ClockPpuAddress(0x1000);
        Assert.Equal(expectedAfterSecondClock, cartridge.IrqPending);
    }

    private sealed class TemporaryNesImage : IDisposable
    {
        private TemporaryNesImage(string directoryPath, string path)
        {
            DirectoryPath = directoryPath;
            Path = path;
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public static TemporaryNesImage Create(int mapper, int submapper, int prgBanks, int chrBanks)
        {
            var directoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PixelDeck.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var path = System.IO.Path.Combine(directoryPath, $"mapper-{mapper}-{submapper}.nes");
            var image = new byte[16 + (prgBanks * 16_384) + (chrBanks * 8_192)];
            image[0] = (byte)'N';
            image[1] = (byte)'E';
            image[2] = (byte)'S';
            image[3] = 0x1A;
            image[4] = (byte)prgBanks;
            image[5] = (byte)chrBanks;
            image[6] = (byte)((mapper & 0x0F) << 4);
            image[7] = (byte)((mapper & 0xF0) | 0x08);
            image[8] = (byte)((submapper << 4) | ((mapper >> 8) & 0x0F));
            File.WriteAllBytes(path, image);
            return new TemporaryNesImage(directoryPath, path);
        }

        public void Dispose()
        {
            var testRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PixelDeck.Tests"))
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
