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
    public void Mapper5SwitchesEveryPrgModeProtectsWorkRamAndRestoresState()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 5,
            submapper: 0,
            prgBanks: 16,
            chrBanks: 16,
            prgRamShift: 7);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 32; bank++)
        {
            Array.Fill(bytes, (byte)(0x20 + bank), 16 + (bank * 8_192), 8_192);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        Assert.Equal(0x3F, cartridge.CpuRead(0xE000));

        cartridge.CpuWrite(0x5100, 3);
        cartridge.CpuWrite(0x5114, 0x83);
        cartridge.CpuWrite(0x5115, 0x84);
        cartridge.CpuWrite(0x5116, 0x85);
        cartridge.CpuWrite(0x5117, 0x86);
        Assert.Equal(0x23, cartridge.CpuRead(0x8000));
        Assert.Equal(0x24, cartridge.CpuRead(0xA000));
        Assert.Equal(0x25, cartridge.CpuRead(0xC000));
        Assert.Equal(0x26, cartridge.CpuRead(0xE000));

        cartridge.CpuWrite(0x5100, 2);
        cartridge.CpuWrite(0x5115, 0x88);
        cartridge.CpuWrite(0x5116, 0x8A);
        cartridge.CpuWrite(0x5117, 0x8B);
        Assert.Equal(0x28, cartridge.CpuRead(0x8000));
        Assert.Equal(0x29, cartridge.CpuRead(0xA000));
        Assert.Equal(0x2A, cartridge.CpuRead(0xC000));
        Assert.Equal(0x2B, cartridge.CpuRead(0xE000));

        cartridge.CpuWrite(0x5100, 1);
        cartridge.CpuWrite(0x5115, 0x8C);
        cartridge.CpuWrite(0x5117, 0x8E);
        Assert.Equal(0x2C, cartridge.CpuRead(0x8000));
        Assert.Equal(0x2D, cartridge.CpuRead(0xA000));
        Assert.Equal(0x2E, cartridge.CpuRead(0xC000));
        Assert.Equal(0x2F, cartridge.CpuRead(0xE000));

        cartridge.CpuWrite(0x5100, 0);
        cartridge.CpuWrite(0x5117, 0x93);
        Assert.Equal(0x30, cartridge.CpuRead(0x8000));
        Assert.Equal(0x31, cartridge.CpuRead(0xA000));
        Assert.Equal(0x32, cartridge.CpuRead(0xC000));
        Assert.Equal(0x33, cartridge.CpuRead(0xE000));

        cartridge.CpuWrite(0x5113, 0);
        cartridge.CpuWrite(0x6000, 0x55);
        Assert.Equal(0, cartridge.CpuRead(0x6000));
        cartridge.CpuWrite(0x5102, 0x02);
        cartridge.CpuWrite(0x5103, 0x01);
        cartridge.CpuWrite(0x6000, 0xA5);
        Assert.Equal(0xA5, cartridge.CpuRead(0x6000));

        var state = SaveCartridgeState(cartridge);
        cartridge.CpuWrite(0x5100, 3);
        cartridge.CpuWrite(0x5114, 0x81);
        cartridge.CpuWrite(0x6000, 0x5A);
        LoadCartridgeState(cartridge, state);

        Assert.Equal(0x30, cartridge.CpuRead(0x8000));
        Assert.Equal(0xA5, cartridge.CpuRead(0x6000));
    }

    [Fact]
    public void Mapper5SelectsSpriteBackgroundAndExtendedAttributeChrBanks()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 5,
            submapper: 0,
            prgBanks: 16,
            chrBanks: 16);
        var bytes = File.ReadAllBytes(image.Path);
        var chrOffset = 16 + (16 * 16_384);
        for (var bank = 0; bank < 128; bank++)
        {
            Array.Fill(bytes, (byte)bank, chrOffset + (bank * 1_024), 1_024);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0x5101, 3);
        cartridge.CpuWrite(0x5120, 1);
        cartridge.CpuWrite(0x5128, 9);
        cartridge.SetPpuControl(0x20);

        Assert.Equal(1, cartridge.PpuRead(0, PpuAccessKind.Sprite));
        Assert.Equal(9, cartridge.PpuRead(0, PpuAccessKind.Background));

        cartridge.SetPpuControl(0);
        Assert.Equal(1, cartridge.PpuRead(0, PpuAccessKind.Background));

        var nametableRam = new byte[2_048];
        cartridge.CpuWrite(0x5104, 0);
        cartridge.CpuWrite(0x5105, 0xE4);
        Assert.True(cartridge.TryPpuWriteNametable(0x2001, 0x11, nametableRam));
        Assert.True(cartridge.TryPpuWriteNametable(0x2401, 0x22, nametableRam));
        Assert.True(cartridge.TryPpuWriteNametable(0x2801, 0x33, nametableRam));
        cartridge.CpuWrite(0x5106, 0x44);
        cartridge.CpuWrite(0x5107, 0x02);

        Assert.True(cartridge.TryPpuReadNametable(
            0x2001, PpuAccessKind.Background, nametableRam, out var ciramA));
        Assert.True(cartridge.TryPpuReadNametable(
            0x2401, PpuAccessKind.Background, nametableRam, out var ciramB));
        Assert.True(cartridge.TryPpuReadNametable(
            0x2801, PpuAccessKind.Background, nametableRam, out var exRam));
        Assert.True(cartridge.TryPpuReadNametable(
            0x2C01, PpuAccessKind.Background, nametableRam, out var fillTile));
        Assert.True(cartridge.TryPpuReadNametable(
            0x2FC0, PpuAccessKind.Background, nametableRam, out var fillAttribute));

        Assert.Equal(0x11, ciramA);
        Assert.Equal(0x22, ciramB);
        Assert.Equal(0x33, exRam);
        Assert.Equal(0x44, fillTile);
        Assert.Equal(0xAA, fillAttribute);

        cartridge.CpuWrite(0x5104, 1);
        cartridge.ClockPpuPosition(0, 0, renderingEnabled: true);
        cartridge.CpuWrite(0x5C00, 0xC2);
        Assert.True(cartridge.TryPpuReadNametable(
            0x2000, PpuAccessKind.Background, nametableRam, out _));
        Assert.True(cartridge.TryPpuReadNametable(
            0x23C0, PpuAccessKind.Background, nametableRam, out var extendedAttribute));

        Assert.Equal(0xFF, extendedAttribute);
        Assert.Equal(8, cartridge.PpuRead(0, PpuAccessKind.Background));
    }

    [Fact]
    public void Mapper5RaisesAndAcknowledgesScanlineIrqAndMultiplies()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 5,
            submapper: 0,
            prgBanks: 16,
            chrBanks: 16);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0x5203, 2);
        cartridge.CpuWrite(0x5204, 0x80);

        cartridge.ClockPpuPosition(0, 0, renderingEnabled: true);
        cartridge.ClockPpuPosition(1, 0, renderingEnabled: true);
        Assert.False(cartridge.IrqPending);
        cartridge.ClockPpuPosition(2, 0, renderingEnabled: true);
        Assert.True(cartridge.IrqPending);

        Assert.Equal(0xC0, cartridge.CpuRead(0x5204));
        Assert.False(cartridge.IrqPending);

        cartridge.CpuWrite(0x5205, 25);
        cartridge.CpuWrite(0x5206, 20);
        Assert.Equal(0xF4, cartridge.CpuRead(0x5205));
        Assert.Equal(0x01, cartridge.CpuRead(0x5206));

        cartridge.ClockPpuPosition(240, 0, renderingEnabled: false);
        Assert.Equal(0, cartridge.CpuRead(0x5204) & 0x40);
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
    public void Mapper11AppliesBusConflictsAndSwitchesColorDreamsPrgAndChr()
    {
        using var image = TemporaryNesImage.Create(mapper: 11, submapper: 0, prgBanks: 8, chrBanks: 8);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 4; bank++)
        {
            Array.Fill(bytes, (byte)(0x60 + bank), 16 + (bank * 32_768), 32_768);
        }

        bytes[16] = 0x21;
        var chrOffset = 16 + (8 * 16_384);
        for (var bank = 0; bank < 8; bank++)
        {
            Array.Fill(bytes, (byte)(0x70 + bank), chrOffset + (bank * 8_192), 8_192);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        cartridge.CpuWrite(0x8000, 0xFF);

        Assert.Equal(0x61, cartridge.CpuRead(0x8000));
        Assert.Equal(0x72, cartridge.PpuRead(0));
    }

    [Fact]
    public void Mapper8SwitchesSuperMagicCardPrgAndChrAndWriteProtectsChr()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 8,
            submapper: 0,
            prgBanks: 8,
            chrBanks: 4,
            prgRamShift: 7);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 4; bank++)
        {
            Array.Fill(bytes, (byte)(0x40 + bank), 16 + (bank * 32_768), 32_768);
        }

        var chrOffset = 16 + (8 * 16_384);
        for (var bank = 0; bank < 4; bank++)
        {
            Array.Fill(bytes, (byte)(0xA0 + bank), chrOffset + (bank * 8_192), 8_192);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        cartridge.CpuWrite(0x8000, 0x32);
        cartridge.CpuWrite(0x6000, 0x5A);
        var state = SaveCartridgeState(cartridge);

        Assert.Equal(0x43, cartridge.CpuRead(0x8000));
        Assert.Equal(0xA2, cartridge.PpuRead(0x0123));
        Assert.Equal(0x5A, cartridge.CpuRead(0x6000));

        cartridge.PpuWrite(0x0123, 0xFF);
        cartridge.CpuWrite(0x8000, 0x00);
        cartridge.CpuWrite(0x6000, 0x00);
        LoadCartridgeState(cartridge, state);

        Assert.Equal(0x43, cartridge.CpuRead(0x8000));
        Assert.Equal(0xA2, cartridge.PpuRead(0x0123));
        Assert.Equal(0x5A, cartridge.CpuRead(0x6000));
    }

    [Fact]
    public void Mapper15ImplementsAllFourK1029PrgModesAndChrProtection()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 15,
            submapper: 0,
            prgBanks: 64,
            chrBanks: 0);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 128; bank++)
        {
            Array.Fill(bytes, (byte)(0x20 + bank), 16 + (bank * 8_192), 8_192);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        cartridge.CpuWrite(0x8000, 0x0B);
        Assert.Equal(0x34, cartridge.CpuRead(0x8000));
        Assert.Equal(0x36, cartridge.CpuRead(0xC000));
        cartridge.PpuWrite(0x0123, 0xA5);
        Assert.Equal(0, cartridge.PpuRead(0x0123));

        cartridge.CpuWrite(0x8001, 0x12);
        Assert.Equal(0x44, cartridge.CpuRead(0x8000));
        Assert.Equal(0x4E, cartridge.CpuRead(0xC000));
        cartridge.PpuWrite(0x0123, 0xA5);
        Assert.Equal(0xA5, cartridge.PpuRead(0x0123));

        cartridge.CpuWrite(0x8002, 0x85);
        var state = SaveCartridgeState(cartridge);
        Assert.Equal(0x2B, cartridge.CpuRead(0x8000));
        Assert.Equal(0x2B, cartridge.CpuRead(0xA000));
        Assert.Equal(0x2B, cartridge.CpuRead(0xC000));
        Assert.Equal(0x2B, cartridge.CpuRead(0xE000));

        cartridge.CpuWrite(0x8003, 0x49);
        Assert.Equal(0x32, cartridge.CpuRead(0x8000));
        Assert.Equal(0x32, cartridge.CpuRead(0xC000));
        Assert.Equal(NametableMirroring.Horizontal, cartridge.Mirroring);

        LoadCartridgeState(cartridge, state);
        Assert.Equal(0x2B, cartridge.CpuRead(0x8000));
        Assert.Equal(NametableMirroring.Vertical, cartridge.Mirroring);
    }

    [Fact]
    public void Mapper15MapperHackCompatibilityProvidesRamAndWritableChr()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 15,
            submapper: 0,
            prgBanks: 16,
            chrBanks: 0,
            prgRamShift: 7);
        var cartridge = Cartridge.Load(image.Path);

        cartridge.CpuWrite(0x6000, 0x5A);
        cartridge.CpuWrite(0x8000, 0x00);
        cartridge.PpuWrite(0x0123, 0xA5);

        Assert.Equal(0x5A, cartridge.CpuRead(0x6000));
        Assert.Equal(0xA5, cartridge.PpuRead(0x0123));
    }

    [Fact]
    public void Mapper90ImplementsJyBankingArithmeticRegistersIrqAndState()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 90,
            submapper: 0,
            prgBanks: 16,
            chrBanks: 32,
            prgRamShift: 7);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 32; bank++)
        {
            Array.Fill(bytes, (byte)(0x20 + bank), 16 + (bank * 8_192), 8_192);
        }

        var chrOffset = 16 + (16 * 16_384);
        for (var bank = 0; bank < 256; bank++)
        {
            Array.Fill(bytes, (byte)bank, chrOffset + (bank * 1_024), 1_024);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        cartridge.CpuWrite(0x8000, 1);
        cartridge.CpuWrite(0x8001, 2);
        cartridge.CpuWrite(0x8002, 3);
        cartridge.CpuWrite(0x8003, 4);
        cartridge.CpuWrite(0xD000, 0x06);
        Assert.Equal(0x21, cartridge.CpuRead(0x8000));
        Assert.Equal(0x22, cartridge.CpuRead(0xA000));
        Assert.Equal(0x23, cartridge.CpuRead(0xC000));
        Assert.Equal(0x24, cartridge.CpuRead(0xE000));

        cartridge.CpuWrite(0xD000, 0x1E);
        cartridge.CpuWrite(0x9003, 0x12);
        cartridge.CpuWrite(0xA003, 0x00);
        cartridge.CpuWrite(0xD001, 0x03);
        Assert.Equal(0x12, cartridge.PpuRead(0x0C00));
        Assert.Equal(NametableMirroring.OneScreenUpper, cartridge.Mirroring);

        cartridge.CpuWrite(0x5800, 12);
        cartridge.CpuWrite(0x5801, 13);
        Assert.Equal(0, cartridge.CpuRead(0x5800));
        for (var cycle = 0; cycle < 8; cycle++)
        {
            cartridge.ClockCpuCycle();
        }

        Assert.Equal(156, cartridge.CpuRead(0x5800));
        Assert.Equal(0, cartridge.CpuRead(0x5801));
        cartridge.CpuWrite(0x5802, 7);
        cartridge.CpuWrite(0x5802, 9);
        Assert.Equal(16, cartridge.CpuRead(0x5802));
        cartridge.CpuWrite(0x5803, 0xA5);
        Assert.Equal(0, cartridge.CpuRead(0x5802));
        Assert.Equal(0xA5, cartridge.CpuRead(0x5803));

        cartridge.CpuWrite(0xC006, 0);
        cartridge.CpuWrite(0xC004, 0xFE);
        cartridge.CpuWrite(0xC005, 0xFF);
        cartridge.CpuWrite(0xC001, 0x40);
        cartridge.CpuWrite(0xC003, 0);
        cartridge.ClockCpuCycle();
        cartridge.ClockCpuCycle();
        Assert.True(cartridge.IrqPending);

        var state = SaveCartridgeState(cartridge);
        cartridge.CpuWrite(0xC002, 0);
        cartridge.CpuWrite(0xD001, 0);
        Assert.False(cartridge.IrqPending);
        LoadCartridgeState(cartridge, state);
        Assert.True(cartridge.IrqPending);
        Assert.Equal(NametableMirroring.OneScreenUpper, cartridge.Mirroring);
    }

    [Fact]
    public void Mapper240SwitchesExpansionSpaceGnromBanksAndRestoresThem()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 240,
            submapper: 0,
            prgBanks: 32,
            chrBanks: 16,
            prgRamShift: 7);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(bytes, (byte)(0x30 + bank), 16 + (bank * 32_768), 32_768);
        }

        var chrOffset = 16 + (32 * 16_384);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(bytes, (byte)(0x70 + bank), chrOffset + (bank * 8_192), 8_192);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        cartridge.CpuWrite(0x4020, 0xA3);
        cartridge.CpuWrite(0x6000, 0x5A);
        var state = SaveCartridgeState(cartridge);
        Assert.Equal(0x3A, cartridge.CpuRead(0x8000));
        Assert.Equal(0x73, cartridge.PpuRead(0));
        Assert.Equal(0x5A, cartridge.CpuRead(0x6000));

        cartridge.CpuWrite(0x5000, 0x00);
        cartridge.CpuWrite(0x6000, 0x00);
        LoadCartridgeState(cartridge, state);
        Assert.Equal(0x3A, cartridge.CpuRead(0x8000));
        Assert.Equal(0x73, cartridge.PpuRead(0));
        Assert.Equal(0x5A, cartridge.CpuRead(0x6000));
    }

    [Fact]
    public void Mapper34DisambiguatesBnromFromNinaAndRestoresTheirBanks()
    {
        using var bnromImage = TemporaryNesImage.Create(
            mapper: 34,
            submapper: 2,
            prgBanks: 8,
            chrBanks: 0);
        var bnromBytes = File.ReadAllBytes(bnromImage.Path);
        for (var bank = 0; bank < 4; bank++)
        {
            Array.Fill(bnromBytes, (byte)(0x30 + bank), 16 + (bank * 32_768), 32_768);
        }

        bnromBytes[16] = 0x03;
        File.WriteAllBytes(bnromImage.Path, bnromBytes);
        var bnrom = Cartridge.Load(bnromImage.Path);
        bnrom.CpuWrite(0x8000, 0xFF);
        Assert.Equal(0x33, bnrom.CpuRead(0x8000));

        using var ninaImage = TemporaryNesImage.Create(
            mapper: 34,
            submapper: 1,
            prgBanks: 8,
            chrBanks: 8);
        var ninaBytes = File.ReadAllBytes(ninaImage.Path);
        for (var bank = 0; bank < 4; bank++)
        {
            Array.Fill(ninaBytes, (byte)(0x40 + bank), 16 + (bank * 32_768), 32_768);
        }

        var chrOffset = 16 + (8 * 16_384);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(ninaBytes, (byte)(0x80 + bank), chrOffset + (bank * 4_096), 4_096);
        }

        File.WriteAllBytes(ninaImage.Path, ninaBytes);
        var nina = Cartridge.Load(ninaImage.Path);
        nina.CpuWrite(0x7FFD, 3);
        nina.CpuWrite(0x7FFE, 3);
        nina.CpuWrite(0x7FFF, 4);
        var state = SaveCartridgeState(nina);

        Assert.Equal(0x43, nina.CpuRead(0x8000));
        Assert.Equal(0x83, nina.PpuRead(0));
        Assert.Equal(0x84, nina.PpuRead(0x1000));

        nina.CpuWrite(0x7FFD, 0);
        LoadCartridgeState(nina, state);
        Assert.Equal(0x43, nina.CpuRead(0x8000));

        using var legacyHybridImage = TemporaryNesImage.Create(
            mapper: 34,
            submapper: 0,
            prgBanks: 8,
            chrBanks: 8);
        var hybridBytes = File.ReadAllBytes(legacyHybridImage.Path);
        for (var bank = 0; bank < 4; bank++)
        {
            Array.Fill(hybridBytes, (byte)(0x50 + bank), 16 + (bank * 32_768), 32_768);
        }

        File.WriteAllBytes(legacyHybridImage.Path, hybridBytes);
        var legacyHybrid = Cartridge.Load(legacyHybridImage.Path);
        legacyHybrid.CpuWrite(0x8000, 3);
        Assert.Equal(0x53, legacyHybrid.CpuRead(0x8000));
    }

    [Fact]
    public void Mapper71SwitchesCamericaPrgAndLazilyEnablesFireHawkMirroring()
    {
        using var image = TemporaryNesImage.Create(mapper: 71, submapper: 0, prgBanks: 8, chrBanks: 0);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 8; bank++)
        {
            Array.Fill(bytes, (byte)(0x50 + bank), 16 + (bank * 16_384), 16_384);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        Assert.Equal(NametableMirroring.Horizontal, cartridge.Mirroring);
        cartridge.CpuWrite(0xC000, 2);
        Assert.Equal(0x52, cartridge.CpuRead(0x8000));
        Assert.Equal(0x57, cartridge.CpuRead(0xC000));

        cartridge.CpuWrite(0x9000, 0x10);
        Assert.Equal(NametableMirroring.OneScreenUpper, cartridge.Mirroring);
    }

    [Fact]
    public void Mapper79DecodesOnlyNinaRegisterAddressesAndSwitchesPrgAndChr()
    {
        using var image = TemporaryNesImage.Create(mapper: 79, submapper: 0, prgBanks: 4, chrBanks: 8);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 2; bank++)
        {
            Array.Fill(bytes, (byte)(0x20 + bank), 16 + (bank * 32_768), 32_768);
        }

        var chrOffset = 16 + (4 * 16_384);
        for (var bank = 0; bank < 8; bank++)
        {
            Array.Fill(bytes, (byte)(0xA0 + bank), chrOffset + (bank * 8_192), 8_192);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        cartridge.CpuWrite(0x4000, 0x0D);
        Assert.Equal(0x20, cartridge.CpuRead(0x8000));
        Assert.Equal(0xA0, cartridge.PpuRead(0));

        cartridge.CpuWrite(0x4100, 0x0D);
        Assert.Equal(0x21, cartridge.CpuRead(0x8000));
        Assert.Equal(0xA5, cartridge.PpuRead(0));
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    public void Mmc2AndMmc4SwitchLatchedChrBanksAndTheirDistinctPrgWindows(
        int mapper,
        bool isMmc4)
    {
        using var image = TemporaryNesImage.Create(mapper, submapper: 0, prgBanks: 16, chrBanks: 16);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 32; bank++)
        {
            Array.Fill(bytes, (byte)(0x20 + bank), 16 + (bank * 8_192), 8_192);
        }

        var chrOffset = 16 + (16 * 16_384);
        for (var bank = 0; bank < 32; bank++)
        {
            Array.Fill(bytes, (byte)(0x80 + bank), chrOffset + (bank * 4_096), 4_096);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0xB000, 1);
        cartridge.CpuWrite(0xC000, 2);
        cartridge.CpuWrite(0xD000, 3);
        cartridge.CpuWrite(0xE000, 4);

        Assert.Equal(0x82, cartridge.PpuRead(0));
        Assert.Equal(0x84, cartridge.PpuRead(0x1000));
        _ = cartridge.PpuRead(0x0FD8);
        _ = cartridge.PpuRead(0x1FD8);
        Assert.Equal(0x81, cartridge.PpuRead(0));
        Assert.Equal(0x83, cartridge.PpuRead(0x1000));

        cartridge.CpuWrite(0xA000, 5);
        Assert.Equal(isMmc4 ? 0x2A : 0x25, cartridge.CpuRead(0x8000));
        Assert.Equal(isMmc4 ? 0x2B : 0x3D, cartridge.CpuRead(0xA000));
        Assert.Equal(0x3E, cartridge.CpuRead(0xC000));

        cartridge.CpuWrite(0xF000, 1);
        Assert.Equal(NametableMirroring.Horizontal, cartridge.Mirroring);
    }

    [Fact]
    public void Mapper13BanksTheUpperHalfOfItsSixteenKilobytesOfChrRam()
    {
        using var image = TemporaryNesImage.Create(mapper: 13, submapper: 0, prgBanks: 2, chrBanks: 0);
        var bytes = File.ReadAllBytes(image.Path);
        bytes[16] = 0xFF;
        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        cartridge.PpuWrite(0x1000, 0xA1);
        cartridge.CpuWrite(0x8000, 2);
        cartridge.PpuWrite(0x1000, 0xB2);
        Assert.Equal(0xB2, cartridge.PpuRead(0x1000));

        cartridge.CpuWrite(0x8000, 0);
        Assert.Equal(0xA1, cartridge.PpuRead(0x1000));
    }

    [Fact]
    public void Mapper22UsesVrc2aAddressLinesChrShiftAndOneBitLatch()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 22,
            submapper: 0,
            prgBanks: 8,
            chrBanks: 16);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(bytes, (byte)(0x40 + bank), 16 + (bank * 8_192), 8_192);
        }

        var chrOffset = 16 + (8 * 16_384);
        for (var bank = 0; bank < 128; bank++)
        {
            Array.Fill(bytes, (byte)(0x80 + bank), chrOffset + (bank * 1_024), 1_024);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0x8000, 3);
        cartridge.CpuWrite(0xA000, 4);
        cartridge.CpuWrite(0xB000, 2);
        cartridge.CpuWrite(0xB002, 1);
        cartridge.CpuWrite(0xB001, 8);
        cartridge.CpuWrite(0xB003, 3);
        cartridge.CpuWrite(0x9000, 1);
        cartridge.CpuWrite(0x6000, 1);

        Assert.Equal(0x43, cartridge.CpuRead(0x8000));
        Assert.Equal(0x44, cartridge.CpuRead(0xA000));
        Assert.Equal(0x4E, cartridge.CpuRead(0xC000));
        Assert.Equal(0x4F, cartridge.CpuRead(0xE000));
        Assert.Equal(0x89, cartridge.PpuRead(0x0000));
        Assert.Equal(0x9C, cartridge.PpuRead(0x0400));
        Assert.Equal(NametableMirroring.Horizontal, cartridge.Mirroring);
        Assert.Equal(0x61, cartridge.CpuRead(0x6100));
    }

    [Fact]
    public void Mapper21Vrc4aSwapsPrgBanksAndRestoresCycleIrqState()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 21,
            submapper: 1,
            prgBanks: 8,
            chrBanks: 16,
            prgRamShift: 7);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(bytes, (byte)(0x40 + bank), 16 + (bank * 8_192), 8_192);
        }

        var chrOffset = 16 + (8 * 16_384);
        for (var bank = 0; bank < 128; bank++)
        {
            Array.Fill(bytes, (byte)(0x80 + bank), chrOffset + (bank * 1_024), 1_024);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0x8000, 3);
        cartridge.CpuWrite(0xA000, 4);
        cartridge.CpuWrite(0xB000, 2);
        cartridge.CpuWrite(0xB002, 1);
        cartridge.CpuWrite(0x9000, 2);
        cartridge.CpuWrite(0x9004, 3);
        cartridge.CpuWrite(0x6000, 0xA5);

        Assert.Equal(0x4E, cartridge.CpuRead(0x8000));
        Assert.Equal(0x44, cartridge.CpuRead(0xA000));
        Assert.Equal(0x43, cartridge.CpuRead(0xC000));
        Assert.Equal(0x4F, cartridge.CpuRead(0xE000));
        Assert.Equal(0x92, cartridge.PpuRead(0x0000));
        Assert.Equal(NametableMirroring.OneScreenLower, cartridge.Mirroring);
        Assert.Equal(0xA5, cartridge.CpuRead(0x6000));

        cartridge.CpuWrite(0xF000, 0x0E);
        cartridge.CpuWrite(0xF002, 0x0F);
        cartridge.CpuWrite(0xF004, 0x06);
        ClockCpuCycles(cartridge, 1);
        Assert.False(cartridge.IrqPending);
        var state = SaveCartridgeState(cartridge);

        ClockCpuCycles(cartridge, 1);
        Assert.True(cartridge.IrqPending);
        cartridge.CpuWrite(0xF006, 0);
        Assert.False(cartridge.IrqPending);

        LoadCartridgeState(cartridge, state);
        Assert.False(cartridge.IrqPending);
        ClockCpuCycles(cartridge, 1);
        Assert.True(cartridge.IrqPending);
        Assert.Equal(0x92, cartridge.PpuRead(0x0000));
    }

    [Fact]
    public void Mapper21Vrc4ScanlineIrqUsesTheThreeFourOnePrescaler()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 21,
            submapper: 1,
            prgBanks: 8,
            chrBanks: 16);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0xF000, 0x0E);
        cartridge.CpuWrite(0xF002, 0x0F);
        cartridge.CpuWrite(0xF004, 0x03);

        ClockCpuCycles(cartridge, 227);
        Assert.False(cartridge.IrqPending);
        cartridge.ClockCpuCycle();
        Assert.True(cartridge.IrqPending);

        cartridge.CpuWrite(0xF006, 0);
        Assert.False(cartridge.IrqPending);
        ClockCpuCycles(cartridge, 227);
        Assert.True(cartridge.IrqPending);
    }

    [Theory]
    [InlineData(0, 0xB001)]
    [InlineData(1, 0xB001)]
    [InlineData(2, 0xB004)]
    [InlineData(3, 0xB001)]
    public void Mapper23DecodesEveryDeclaredVrc2Vrc4AddressVariant(
        int submapper,
        int highRegisterAddress)
    {
        using var image = TemporaryNesImage.Create(
            mapper: 23,
            submapper: submapper,
            prgBanks: 8,
            chrBanks: 16);
        var bytes = File.ReadAllBytes(image.Path);
        var chrOffset = 16 + (8 * 16_384);
        for (var bank = 0; bank < 128; bank++)
        {
            Array.Fill(bytes, (byte)(0x80 + bank), chrOffset + (bank * 1_024), 1_024);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0xB000, 3);
        cartridge.CpuWrite((ushort)highRegisterAddress, 2);

        Assert.Equal(0xA3, cartridge.PpuRead(0x0000));
    }

    [Fact]
    public void Mapper32SwitchesIremPrgModeAndEightIndependentChrBanks()
    {
        using var image = TemporaryNesImage.Create(mapper: 32, submapper: 0, prgBanks: 4, chrBanks: 2);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 8; bank++)
        {
            Array.Fill(bytes, (byte)(0x20 + bank), 16 + (bank * 8_192), 8_192);
        }

        var chrOffset = 16 + (4 * 16_384);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(bytes, (byte)(0x80 + bank), chrOffset + (bank * 1_024), 1_024);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0x8000, 3);
        cartridge.CpuWrite(0xA000, 4);
        cartridge.CpuWrite(0xB005, 9);

        Assert.Equal(0x23, cartridge.CpuRead(0x8000));
        Assert.Equal(0x24, cartridge.CpuRead(0xA000));
        Assert.Equal(0x89, cartridge.PpuRead(0x1400));

        cartridge.CpuWrite(0x9000, 3);
        Assert.Equal(0x26, cartridge.CpuRead(0x8000));
        Assert.Equal(0x23, cartridge.CpuRead(0xC000));
        Assert.Equal(NametableMirroring.Horizontal, cartridge.Mirroring);
    }

    [Fact]
    public void Mapper33UsesTaitoTwoAndOneKilobyteChrBankUnits()
    {
        using var image = TemporaryNesImage.Create(mapper: 33, submapper: 0, prgBanks: 4, chrBanks: 4);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 8; bank++)
        {
            Array.Fill(bytes, (byte)(0x30 + bank), 16 + (bank * 8_192), 8_192);
        }

        var chrOffset = 16 + (4 * 16_384);
        for (var bank = 0; bank < 32; bank++)
        {
            Array.Fill(bytes, (byte)(0x80 + bank), chrOffset + (bank * 1_024), 1_024);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0x8000, 0x43);
        cartridge.CpuWrite(0x8001, 4);
        cartridge.CpuWrite(0x8002, 5);
        cartridge.CpuWrite(0xA003, 11);

        Assert.Equal(0x33, cartridge.CpuRead(0x8000));
        Assert.Equal(0x34, cartridge.CpuRead(0xA000));
        Assert.Equal(0x8A, cartridge.PpuRead(0));
        Assert.Equal(0x8B, cartridge.PpuRead(0x1C00));
        Assert.Equal(NametableMirroring.Horizontal, cartridge.Mirroring);
    }

    [Fact]
    public void Mapper41CombinesCaltronOuterAndBusConflictedInnerBanks()
    {
        using var image = TemporaryNesImage.Create(mapper: 41, submapper: 0, prgBanks: 16, chrBanks: 16);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 8; bank++)
        {
            Array.Fill(bytes, (byte)(0x40 + bank), 16 + (bank * 32_768), 32_768);
        }

        bytes[16 + (6 * 32_768)] = 0xFF;
        var chrOffset = 16 + (16 * 16_384);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(bytes, (byte)(0xA0 + bank), chrOffset + (bank * 8_192), 8_192);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0x603E, 0);
        cartridge.CpuWrite(0x8000, 3);

        Assert.Equal(0x46, cartridge.CpuRead(0x8001));
        Assert.Equal(0xAF, cartridge.PpuRead(0));
        Assert.Equal(NametableMirroring.Horizontal, cartridge.Mirroring);
    }

    [Fact]
    public void Mapper64SwitchesExtendedBanksAndRestoresItsCycleIrq()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 64,
            submapper: 0,
            prgBanks: 8,
            chrBanks: 2);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(bytes, (byte)(0x40 + bank), 16 + (bank * 8_192), 8_192);
        }

        var chrOffset = 16 + (8 * 16_384);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(bytes, (byte)(0x80 + bank), chrOffset + (bank * 1_024), 1_024);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        WriteRamboBank(cartridge, 6, 3);
        WriteRamboBank(cartridge, 7, 4);
        WriteRamboBank(cartridge, 15, 5);
        Assert.Equal(0x43, cartridge.CpuRead(0x8000));
        Assert.Equal(0x44, cartridge.CpuRead(0xA000));
        Assert.Equal(0x45, cartridge.CpuRead(0xC000));
        Assert.Equal(0x4F, cartridge.CpuRead(0xE000));

        cartridge.CpuWrite(0x8000, 0x40);
        Assert.Equal(0x45, cartridge.CpuRead(0x8000));
        Assert.Equal(0x43, cartridge.CpuRead(0xC000));

        WriteRamboBank(cartridge, 0, 2, oneKilobyteChrMode: true);
        WriteRamboBank(cartridge, 8, 7, oneKilobyteChrMode: true);
        Assert.Equal(0x82, cartridge.PpuRead(0x0000));
        Assert.Equal(0x87, cartridge.PpuRead(0x0400));

        cartridge.CpuWrite(0xA000, 1);
        Assert.Equal(NametableMirroring.Horizontal, cartridge.Mirroring);
        cartridge.CpuWrite(0xC000, 1);
        cartridge.CpuWrite(0xC001, 1);
        cartridge.CpuWrite(0xE001, 0);
        ClockCpuCycles(cartridge, 8);
        Assert.False(cartridge.IrqPending);
        var state = SaveCartridgeState(cartridge);

        ClockCpuCycles(cartridge, 4);
        Assert.True(cartridge.IrqPending);
        cartridge.CpuWrite(0xE000, 0);
        Assert.False(cartridge.IrqPending);

        LoadCartridgeState(cartridge, state);
        Assert.False(cartridge.IrqPending);
        ClockCpuCycles(cartridge, 4);
        Assert.True(cartridge.IrqPending);
        Assert.Equal(0x82, cartridge.PpuRead(0x0000));
        Assert.Equal(0x87, cartridge.PpuRead(0x0400));
    }

    [Fact]
    public void Mapper64ScanlineIrqUsesFilteredA12AndDelayedAssertion()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 64,
            submapper: 0,
            prgBanks: 8,
            chrBanks: 2);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0xC000, 1);
        cartridge.CpuWrite(0xC001, 0);
        cartridge.CpuWrite(0xE001, 0);

        ClockPpuAddress(cartridge, 0x0000, 8);
        cartridge.ClockPpuAddress(0x1000);
        ClockPpuAddress(cartridge, 0x0000, 8);
        cartridge.ClockPpuAddress(0x1000);
        Assert.False(cartridge.IrqPending);

        ClockCpuCycles(cartridge, 3);
        Assert.False(cartridge.IrqPending);
        cartridge.ClockCpuCycle();
        Assert.True(cartridge.IrqPending);
    }

    [Fact]
    public void Mapper69BanksEveryWindowAndRestoresItsDownCounterIrq()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 69,
            submapper: 0,
            prgBanks: 8,
            chrBanks: 2,
            prgRamShift: 7);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(bytes, (byte)(0x40 + bank), 16 + (bank * 8_192), 8_192);
        }

        var chrOffset = 16 + (8 * 16_384);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(bytes, (byte)(0x80 + bank), chrOffset + (bank * 1_024), 1_024);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);
        WriteFme7Register(cartridge, 0, 6);
        WriteFme7Register(cartridge, 7, 12);
        WriteFme7Register(cartridge, 8, 2);
        WriteFme7Register(cartridge, 9, 3);
        WriteFme7Register(cartridge, 10, 4);
        WriteFme7Register(cartridge, 11, 5);
        WriteFme7Register(cartridge, 12, 3);

        Assert.Equal(0x42, cartridge.CpuRead(0x6000));
        Assert.Equal(0x43, cartridge.CpuRead(0x8000));
        Assert.Equal(0x44, cartridge.CpuRead(0xA000));
        Assert.Equal(0x45, cartridge.CpuRead(0xC000));
        Assert.Equal(0x4F, cartridge.CpuRead(0xE000));
        Assert.Equal(0x86, cartridge.PpuRead(0x0000));
        Assert.Equal(0x8C, cartridge.PpuRead(0x1C00));
        Assert.Equal(NametableMirroring.OneScreenUpper, cartridge.Mirroring);

        WriteFme7Register(cartridge, 8, 0xC0);
        cartridge.CpuWrite(0x6000, 0xA5);
        Assert.Equal(0xA5, cartridge.CpuRead(0x6000));
        WriteFme7Register(cartridge, 8, 0x40);
        Assert.Equal(0, cartridge.CpuRead(0x6000));

        WriteFme7Register(cartridge, 14, 1);
        WriteFme7Register(cartridge, 15, 0);
        WriteFme7Register(cartridge, 13, 0x81);
        cartridge.ClockCpuCycle();
        Assert.False(cartridge.IrqPending);
        var state = SaveCartridgeState(cartridge);

        cartridge.ClockCpuCycle();
        Assert.True(cartridge.IrqPending);
        WriteFme7Register(cartridge, 13, 0);
        Assert.False(cartridge.IrqPending);

        LoadCartridgeState(cartridge, state);
        Assert.False(cartridge.IrqPending);
        cartridge.ClockCpuCycle();
        Assert.True(cartridge.IrqPending);
        Assert.Equal(0x86, cartridge.PpuRead(0x0000));
    }

    [Fact]
    public void Mapper69Sunsoft5BAudioClocksToneNoiseAndEnvelope()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 69,
            submapper: 0,
            prgBanks: 8,
            chrBanks: 2);
        var cartridge = Cartridge.Load(image.Path);

        WriteSunsoftAudio(cartridge, 0, 1);
        WriteSunsoftAudio(cartridge, 1, 0);
        WriteSunsoftAudio(cartridge, 7, 0x3E);
        WriteSunsoftAudio(cartridge, 8, 0x0F);
        Assert.Equal(0, cartridge.ExpansionAudioOutput);
        ClockCpuCycles(cartridge, 16);
        Assert.True(cartridge.ExpansionAudioOutput > 0.3f);
        var toneState = SaveCartridgeState(cartridge);
        ClockCpuCycles(cartridge, 16);
        Assert.Equal(0, cartridge.ExpansionAudioOutput);
        LoadCartridgeState(cartridge, toneState);
        Assert.True(cartridge.ExpansionAudioOutput > 0.3f);

        WriteSunsoftAudio(cartridge, 6, 1);
        WriteSunsoftAudio(cartridge, 7, 0x37);
        Assert.True(cartridge.ExpansionAudioOutput > 0.3f);
        ClockCpuCycles(cartridge, 32);
        Assert.Equal(0, cartridge.ExpansionAudioOutput);

        WriteSunsoftAudio(cartridge, 7, 0x3F);
        WriteSunsoftAudio(cartridge, 8, 0x10);
        WriteSunsoftAudio(cartridge, 11, 1);
        WriteSunsoftAudio(cartridge, 12, 0);
        WriteSunsoftAudio(cartridge, 13, 0x0C);
        Assert.Equal(0, cartridge.ExpansionAudioOutput);
        ClockCpuCycles(cartridge, 32);
        Assert.True(cartridge.ExpansionAudioOutput > 0);

        cartridge.CpuWrite(0xC000, 0x18);
        cartridge.CpuWrite(0xE000, 0);
        cartridge.CpuWrite(0xC000, 8);
        Assert.True(cartridge.ExpansionAudioOutput > 0);
    }

    [Fact]
    public void Mapper19BanksEveryWindowMapsCiramAndRestoresItsCycleIrq()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 19,
            submapper: 0,
            prgBanks: 8,
            chrBanks: 16,
            prgRamShift: 7);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(bytes, (byte)(0x40 + bank), 16 + (bank * 8_192), 8_192);
        }

        var chrOffset = 16 + (8 * 16_384);
        for (var bank = 0; bank < 128; bank++)
        {
            Array.Fill(bytes, (byte)(0x80 + bank), chrOffset + (bank * 1_024), 1_024);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0x8000, 6);
        cartridge.CpuWrite(0xB800, 12);
        cartridge.CpuWrite(0xC000, 7);
        cartridge.CpuWrite(0xE000, 3);
        cartridge.CpuWrite(0xE800, 4);
        cartridge.CpuWrite(0xF000, 5);

        Assert.Equal(0x43, cartridge.CpuRead(0x8000));
        Assert.Equal(0x44, cartridge.CpuRead(0xA000));
        Assert.Equal(0x45, cartridge.CpuRead(0xC000));
        Assert.Equal(0x4F, cartridge.CpuRead(0xE000));
        Assert.Equal(0x86, cartridge.PpuRead(0x0000));
        Assert.Equal(0x8C, cartridge.PpuRead(0x1C00));

        var nametableRam = new byte[2_048];
        Assert.True(cartridge.TryPpuReadNametable(
            0x2000, PpuAccessKind.Background, nametableRam, out var chrNametable));
        Assert.Equal(0x87, chrNametable);

        cartridge.CpuWrite(0xC000, 0xE1);
        Assert.True(cartridge.TryPpuWriteNametable(0x2002, 0xA1, nametableRam));
        cartridge.CpuWrite(0x8000, 0xE1);
        Assert.Equal(0xA1, cartridge.PpuRead(0x0002));
        cartridge.CpuWrite(0xE800, 0x44);
        Assert.Equal(0xE1, cartridge.PpuRead(0x0002));

        cartridge.CpuWrite(0xF800, 0x40);
        cartridge.CpuWrite(0x6000, 0xA5);
        Assert.Equal(0xA5, cartridge.CpuRead(0x6000));
        cartridge.CpuWrite(0xF800, 0x41);
        cartridge.CpuWrite(0x6000, 0xB6);
        Assert.Equal(0xA5, cartridge.CpuRead(0x6000));

        cartridge.CpuWrite(0x5000, 0xFE);
        cartridge.CpuWrite(0x5800, 0xFF);
        var state = SaveCartridgeState(cartridge);
        cartridge.ClockCpuCycle();
        Assert.True(cartridge.IrqPending);
        Assert.Equal(0x7F, cartridge.CpuRead(0x5800));
        cartridge.CpuWrite(0x5000, 0);
        Assert.False(cartridge.IrqPending);

        LoadCartridgeState(cartridge, state);
        Assert.False(cartridge.IrqPending);
        cartridge.ClockCpuCycle();
        Assert.True(cartridge.IrqPending);
        Assert.Equal(0xA1, cartridge.TryPpuReadNametable(
            0x2002, PpuAccessKind.Background, nametableRam, out var restoredCiram)
            ? restoredCiram
            : 0);
    }

    [Fact]
    public void Mapper19InternalPortAndNamco163AudioPreserveSerialOutput()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 19,
            submapper: 0,
            prgBanks: 8,
            chrBanks: 16);
        var cartridge = Cartridge.Load(image.Path);

        cartridge.CpuWrite(0xF800, 2);
        cartridge.CpuWrite(0x4FFF, 0x5A);
        cartridge.CpuWrite(0xF800, 2);
        Assert.Equal(0x5A, cartridge.CpuRead(0x48A5));
        cartridge.CpuWrite(0x57FF, 0x34);
        Assert.Equal(0x34, cartridge.CpuRead(0x5001));

        cartridge.CpuWrite(0xF800, 0xFE);
        cartridge.CpuWrite(0x4800, 0xAA);
        cartridge.CpuWrite(0x4800, 0xBB);
        cartridge.CpuWrite(0x4800, 0xCC);
        cartridge.CpuWrite(0xF800, 0x7E);
        Assert.Equal(0xAA, cartridge.CpuRead(0x4800));
        cartridge.CpuWrite(0xF800, 0x7F);
        Assert.Equal(0xCC, cartridge.CpuRead(0x4800));

        WriteNamco163Ram(cartridge, 0x00, 0xF0);
        WriteNamco163Ram(cartridge, 0x78, 0);
        WriteNamco163Ram(cartridge, 0x79, 0);
        WriteNamco163Ram(cartridge, 0x7A, 0);
        WriteNamco163Ram(cartridge, 0x7B, 0);
        WriteNamco163Ram(cartridge, 0x7C, 0);
        WriteNamco163Ram(cartridge, 0x7D, 0);
        WriteNamco163Ram(cartridge, 0x7E, 1);
        WriteNamco163Ram(cartridge, 0x7F, 0x0F);
        ClockCpuCycles(cartridge, 15);
        Assert.InRange(cartridge.ExpansionAudioOutput, 0.65f, 0.67f);
        var state = SaveCartridgeState(cartridge);

        WriteNamco163Ram(cartridge, 0x00, 0x88);
        ClockCpuCycles(cartridge, 15);
        Assert.Equal(0, cartridge.ExpansionAudioOutput);
        LoadCartridgeState(cartridge, state);
        Assert.InRange(cartridge.ExpansionAudioOutput, 0.65f, 0.67f);

        cartridge.CpuWrite(0xE000, 0x40);
        Assert.Equal(0, cartridge.ExpansionAudioOutput);
        cartridge.CpuWrite(0xE000, 0);
        Assert.InRange(cartridge.ExpansionAudioOutput, 0.65f, 0.67f);

        WriteNamco163Ram(cartridge, 0x7E, 0);
        ClockCpuCycles(cartridge, 15);
        Assert.InRange(cartridge.ExpansionAudioOutput, -0.76f, -0.74f);
    }

    [Fact]
    public void Mapper75CombinesVrc1ChrHighBitsWithSeparateLowRegisters()
    {
        using var image = TemporaryNesImage.Create(mapper: 75, submapper: 0, prgBanks: 8, chrBanks: 16);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(bytes, (byte)(0x40 + bank), 16 + (bank * 8_192), 8_192);
        }

        var chrOffset = 16 + (8 * 16_384);
        for (var bank = 0; bank < 32; bank++)
        {
            Array.Fill(bytes, (byte)(0x80 + bank), chrOffset + (bank * 4_096), 4_096);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0x8000, 3);
        cartridge.CpuWrite(0xA000, 4);
        cartridge.CpuWrite(0xC000, 5);
        cartridge.CpuWrite(0xE000, 2);
        cartridge.CpuWrite(0xF000, 3);
        cartridge.CpuWrite(0x9000, 7);

        Assert.Equal(0x43, cartridge.CpuRead(0x8000));
        Assert.Equal(0x44, cartridge.CpuRead(0xA000));
        Assert.Equal(0x45, cartridge.CpuRead(0xC000));
        Assert.Equal(0x92, cartridge.PpuRead(0));
        Assert.Equal(0x93, cartridge.PpuRead(0x1000));
        Assert.Equal(NametableMirroring.Horizontal, cartridge.Mirroring);
    }

    [Fact]
    public void Mapper113CombinesItsSeparatedPrgChrAndMirroringBits()
    {
        using var image = TemporaryNesImage.Create(mapper: 113, submapper: 0, prgBanks: 16, chrBanks: 16);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 8; bank++)
        {
            Array.Fill(bytes, (byte)(0x40 + bank), 16 + (bank * 32_768), 32_768);
        }

        var chrOffset = 16 + (16 * 16_384);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(bytes, (byte)(0x80 + bank), chrOffset + (bank * 8_192), 8_192);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0x4100, 0xEA);

        Assert.Equal(0x45, cartridge.CpuRead(0x8000));
        Assert.Equal(0x8A, cartridge.PpuRead(0));
        Assert.Equal(NametableMirroring.Vertical, cartridge.Mirroring);
    }

    [Fact]
    public void Mapper118UsesChrBankBitSevenToSelectEachNametable()
    {
        using var image = TemporaryNesImage.Create(mapper: 118, submapper: 0, prgBanks: 8, chrBanks: 16);
        var cartridge = Cartridge.Load(image.Path);
        var nametableRam = new byte[2_048];

        cartridge.CpuWrite(0x8000, 0);
        cartridge.CpuWrite(0x8001, 0x80);
        cartridge.CpuWrite(0x8000, 1);
        cartridge.CpuWrite(0x8001, 0x00);

        Assert.True(cartridge.TryPpuWriteNametable(0x2001, 0xA1, nametableRam));
        Assert.True(cartridge.TryPpuWriteNametable(0x2401, 0xA2, nametableRam));
        Assert.True(cartridge.TryPpuWriteNametable(0x2801, 0xB1, nametableRam));
        Assert.True(cartridge.TryPpuWriteNametable(0x2C01, 0xB2, nametableRam));

        Assert.Equal(0xA2, nametableRam[0x401]);
        Assert.Equal(0xB2, nametableRam[0x001]);
        Assert.True(cartridge.TryPpuReadNametable(
            0x2001, PpuAccessKind.Background, nametableRam, out var first));
        Assert.True(cartridge.TryPpuReadNametable(
            0x2801, PpuAccessKind.Background, nametableRam, out var third));
        Assert.Equal(0xA2, first);
        Assert.Equal(0xB2, third);
    }

    [Fact]
    public void Mapper119SelectsChrRamPerTqromBankAndRestoresIt()
    {
        using var image = TemporaryNesImage.Create(mapper: 119, submapper: 0, prgBanks: 8, chrBanks: 8);
        var bytes = File.ReadAllBytes(image.Path);
        var chrOffset = 16 + (8 * 16_384);
        Array.Fill(bytes, (byte)0x31, chrOffset, 1_024);
        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        cartridge.CpuWrite(0x8000, 2);
        cartridge.CpuWrite(0x8001, 0x40);
        cartridge.PpuWrite(0x1000, 0xD5);
        Assert.Equal(0xD5, cartridge.PpuRead(0x1000));
        var state = SaveCartridgeState(cartridge);

        cartridge.PpuWrite(0x1000, 0xE6);
        cartridge.CpuWrite(0x8001, 0x00);
        Assert.Equal(0x31, cartridge.PpuRead(0x1000));

        LoadCartridgeState(cartridge, state);
        Assert.Equal(0xD5, cartridge.PpuRead(0x1000));
    }

    [Fact]
    public void Mapper228UsesAddressLinesForAction52PrgChrModeAndMirroring()
    {
        using var image = TemporaryNesImage.Create(mapper: 228, submapper: 0, prgBanks: 96, chrBanks: 64);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 96; bank++)
        {
            Array.Fill(bytes, (byte)bank, 16 + (bank * 16_384), 16_384);
        }

        var chrOffset = 16 + (96 * 16_384);
        for (var bank = 0; bank < 64; bank++)
        {
            Array.Fill(bytes, (byte)(0x80 + bank), chrOffset + (bank * 8_192), 8_192);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);
        cartridge.CpuWrite(0x88C5, 2);

        Assert.Equal(34, cartridge.CpuRead(0x8000));
        Assert.Equal(35, cartridge.CpuRead(0xC000));
        Assert.Equal(0x96, cartridge.PpuRead(0));
        Assert.Equal(NametableMirroring.Vertical, cartridge.Mirroring);

        cartridge.CpuWrite(0xA8E5, 2);
        Assert.Equal(35, cartridge.CpuRead(0x8000));
        Assert.Equal(35, cartridge.CpuRead(0xC000));
        Assert.Equal(NametableMirroring.Horizontal, cartridge.Mirroring);
    }

    [Fact]
    public void Mapper232CombinesQuattroOuterAndInnerPrgBanks()
    {
        using var image = TemporaryNesImage.Create(mapper: 232, submapper: 0, prgBanks: 16, chrBanks: 0);
        var bytes = File.ReadAllBytes(image.Path);
        for (var bank = 0; bank < 16; bank++)
        {
            Array.Fill(bytes, (byte)(0xC0 + bank), 16 + (bank * 16_384), 16_384);
        }

        File.WriteAllBytes(image.Path, bytes);
        var cartridge = Cartridge.Load(image.Path);

        Assert.Equal(0xCC, cartridge.CpuRead(0x8000));
        Assert.Equal(0xCF, cartridge.CpuRead(0xC000));

        cartridge.CpuWrite(0x8000, 0x08);
        cartridge.CpuWrite(0xC000, 0x02);

        Assert.Equal(0xC6, cartridge.CpuRead(0x8000));
        Assert.Equal(0xC7, cartridge.CpuRead(0xC000));
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

    private static void WriteRamboBank(
        Cartridge cartridge,
        byte register,
        byte value,
        bool oneKilobyteChrMode = false)
    {
        cartridge.CpuWrite(0x8000, (byte)(register | (oneKilobyteChrMode ? 0x20 : 0)));
        cartridge.CpuWrite(0x8001, value);
    }

    private static void WriteFme7Register(Cartridge cartridge, byte register, byte value)
    {
        cartridge.CpuWrite(0x8000, register);
        cartridge.CpuWrite(0xA000, value);
    }

    private static void WriteSunsoftAudio(Cartridge cartridge, byte register, byte value)
    {
        cartridge.CpuWrite(0xC000, register);
        cartridge.CpuWrite(0xE000, value);
    }

    private static void WriteNamco163Ram(Cartridge cartridge, byte address, byte value)
    {
        cartridge.CpuWrite(0xF800, address);
        cartridge.CpuWrite(0x4800, value);
    }

    private static void ClockCpuCycles(Cartridge cartridge, int count)
    {
        for (var cycle = 0; cycle < count; cycle++)
        {
            cartridge.ClockCpuCycle();
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

        public static TemporaryNesImage Create(
            int mapper,
            int submapper,
            int prgBanks,
            int chrBanks,
            int prgRamShift = 0)
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
            image[10] = (byte)(prgRamShift & 0x0F);
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
