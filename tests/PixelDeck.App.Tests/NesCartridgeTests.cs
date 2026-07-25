using PixelDeck.Emulation.Nes;

namespace PixelDeck.App.Tests;

public sealed class NesCartridgeTests
{
    [Fact]
    public void BatteryBackedProgramRamPersistsAcrossCartridgeInstances()
    {
        using var image = TemporaryNesImage.Create(mapper: 1, battery: true);
        var savePath = Path.Combine(image.DirectoryPath, "game.sav");
        var cartridge = Cartridge.Load(image.Path, savePath);

        Assert.True(cartridge.HasBatteryBackedRam);
        Assert.False(File.Exists(savePath));

        cartridge.CpuWrite(0x6000, 0x5A);
        cartridge.CpuWrite(0x7FFF, 0xC3);
        cartridge.FlushBatterySave();

        Assert.Equal(8_192, new FileInfo(savePath).Length);
        Assert.False(File.Exists(savePath + ".tmp"));

        var reloaded = Cartridge.Load(image.Path, savePath);
        Assert.Equal(0x5A, reloaded.CpuRead(0x6000));
        Assert.Equal(0xC3, reloaded.CpuRead(0x7FFF));
    }

    [Fact]
    public void InterruptedBatteryWritePreservesTheLastCommittedSave()
    {
        using var image = TemporaryNesImage.Create(mapper: 1, battery: true);
        var savePath = Path.Combine(image.DirectoryPath, "game.sav");
        var cartridge = Cartridge.Load(image.Path, savePath);
        cartridge.CpuWrite(0x6000, 0x5A);
        cartridge.FlushBatterySave();

        File.WriteAllBytes(savePath + ".tmp", [0xFF, 0xEE]);

        var reloaded = Cartridge.Load(image.Path, savePath);

        Assert.Equal(0x5A, reloaded.CpuRead(0x6000));
    }

    [Fact]
    public void CompleteTemporaryBatterySaveIsRecoveredWhenCommitWasInterrupted()
    {
        using var image = TemporaryNesImage.Create(mapper: 1, battery: true);
        var savePath = Path.Combine(image.DirectoryPath, "game.sav");
        var temporaryPath = savePath + ".tmp";
        var cartridge = Cartridge.Load(image.Path, savePath);
        cartridge.CpuWrite(0x6000, 0xA7);
        cartridge.FlushBatterySave();
        File.Move(savePath, temporaryPath);

        var reloaded = Cartridge.Load(image.Path, savePath);

        Assert.Equal(0xA7, reloaded.CpuRead(0x6000));
        Assert.True(File.Exists(savePath));
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public void CompleteTemporaryBatterySaveRecoversAInterruptedFinalReplacement()
    {
        using var image = TemporaryNesImage.Create(mapper: 1, battery: true);
        var savePath = Path.Combine(image.DirectoryPath, "game.sav");
        var temporaryPath = savePath + ".tmp";
        var cartridge = Cartridge.Load(image.Path, savePath);
        cartridge.CpuWrite(0x6000, 0xC9);
        cartridge.FlushBatterySave();
        File.Copy(savePath, temporaryPath);
        File.WriteAllBytes(savePath, [0x01, 0x02, 0x03]);

        var reloaded = Cartridge.Load(image.Path, savePath);

        Assert.Equal(0xC9, reloaded.CpuRead(0x6000));
        Assert.Equal(8_192, new FileInfo(savePath).Length);
        Assert.False(File.Exists(temporaryPath));
    }

    [Fact]
    public void TrainerIsCopiedIntoCpuMemoryAtSevenThousand()
    {
        using var image = TemporaryNesImage.Create(mapper: 0, trainer: true);
        var bytes = File.ReadAllBytes(image.Path);
        for (var index = 0; index < 512; index++)
        {
            bytes[16 + index] = (byte)(index ^ 0xA5);
        }

        File.WriteAllBytes(image.Path, bytes);

        var cartridge = Cartridge.Load(image.Path);

        Assert.Equal(0xA5, cartridge.CpuRead(0x7000));
        Assert.Equal(0x5A, cartridge.CpuRead(0x71FF));
    }

    [Fact]
    public void Nes20InspectionReportsRamTimingAndDefaultInputMetadata()
    {
        using var image = TemporaryNesImage.Create(
            mapper: 0,
            nes20: true,
            prgRamShift: 7,
            prgNvRamShift: 7,
            chrRamShift: 7,
            timing: NesTimingMode.Dendy,
            defaultInputDevice: 0x08);

        var info = Cartridge.Inspect(image.Path);

        Assert.True(info.IsNes20);
        Assert.True(info.HasBatteryBackedRam);
        Assert.Equal(8_192, info.PrgRamSize);
        Assert.Equal(8_192, info.PrgNvRamSize);
        Assert.Equal(8_192, info.ChrRamSize);
        Assert.Equal(0, info.ChrNvRamSize);
        Assert.Equal(NesTimingMode.Dendy, info.TimingMode);
        Assert.Equal(0x08, info.DefaultInputDevice);
        Assert.False(info.IsSupported);
        Assert.Contains("NTSC-only", info.CompatibilityWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nes20ExponentMultiplierRomSizesLoadCorrectly()
    {
        using var image = TemporaryNesImage.Create(mapper: 0, nes20: true, exponentPrgSize: true);

        var cartridge = Cartridge.Load(image.Path);

        Assert.Equal(0, cartridge.MapperNumber);
    }

    [Fact]
    public void ArchaicInesGarbageDoesNotInventAMapperOrPalTiming()
    {
        using var image = TemporaryNesImage.Create(mapper: 0);
        var bytes = File.ReadAllBytes(image.Path);
        "DiskDude!"u8.CopyTo(bytes.AsSpan(7, 9));
        File.WriteAllBytes(image.Path, bytes);

        var info = Cartridge.Inspect(image.Path);
        var cartridge = Cartridge.Load(image.Path);

        Assert.Equal(0, info.MapperNumber);
        Assert.Equal(0, cartridge.MapperNumber);
        Assert.Equal(NesTimingMode.Ntsc, info.TimingMode);
        Assert.Equal(8_192, info.PrgRamSize);
        Assert.True(info.IsSupported);
        Assert.Contains("archaic iNES", info.CompatibilityWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StandardInesKeepsARealUpperMapperNibble()
    {
        using var image = TemporaryNesImage.Create(mapper: 64);

        var info = Cartridge.Inspect(image.Path);
        var cartridge = Cartridge.Load(image.Path);

        Assert.Equal(64, info.MapperNumber);
        Assert.Equal(64, cartridge.MapperNumber);
        Assert.True(info.IsSupported);
    }

    [Fact]
    public void ImpossibleLegacyMapper33ChrRamBatteryLayoutIsCorrectedToMmc1()
    {
        using var image = TemporaryNesImage.Create(mapper: 33, battery: true, consoleType: 1);

        var info = Cartridge.Inspect(image.Path);
        var cartridge = Cartridge.Load(image.Path);

        Assert.Equal(1, info.MapperNumber);
        Assert.Equal(1, cartridge.MapperNumber);
        Assert.True(info.IsSupported);
        Assert.Contains("loaded as mapper 1", info.CompatibilityWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImpossibleLegacyMapper8GeometryIsCorrectedToNina06()
    {
        using var image = TemporaryNesImage.Create(mapper: 8, prgBanks: 4, chrBanks: 8);
        var bytes = File.ReadAllBytes(image.Path);
        bytes[6] |= 0x01;
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
        var info = Cartridge.Inspect(image.Path);
        var cartridge = Cartridge.Load(image.Path);

        Assert.Equal(79, info.MapperNumber);
        Assert.Equal(79, cartridge.MapperNumber);
        Assert.True(info.IsSupported);
        Assert.Equal(NametableMirroring.Horizontal, cartridge.Mirroring);
        Assert.Contains("loaded as mapper 79", info.CompatibilityWarning, StringComparison.OrdinalIgnoreCase);

        cartridge.CpuWrite(0x4100, 0x0D);
        Assert.Equal(0x21, cartridge.CpuRead(0x8000));
        Assert.Equal(0xA5, cartridge.PpuRead(0));
    }

    [Fact]
    public void ObsoleteMapper160IsCorrectedToJyCompanyMapper90()
    {
        using var image = TemporaryNesImage.Create(mapper: 160, prgBanks: 16, chrBanks: 32);

        var info = Cartridge.Inspect(image.Path);
        var cartridge = Cartridge.Load(image.Path);

        Assert.Equal(90, info.MapperNumber);
        Assert.Equal(90, cartridge.MapperNumber);
        Assert.True(info.IsSupported);
        Assert.Contains("mapper 90", info.CompatibilityWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReservedTailGarbageCannotInventAnUpperMapperOrConsoleType()
    {
        using var image = TemporaryNesImage.Create(mapper: 0, prgBanks: 2, chrBanks: 1);
        var bytes = File.ReadAllBytes(image.Path);
        bytes[7] = 0xFF;
        "Ni0330"u8.CopyTo(bytes.AsSpan(10, 6));
        File.WriteAllBytes(image.Path, bytes);

        var info = Cartridge.Inspect(image.Path);
        var cartridge = Cartridge.Load(image.Path);

        Assert.Equal(0, info.MapperNumber);
        Assert.Equal(0, cartridge.MapperNumber);
        Assert.Equal(0, info.ConsoleType);
        Assert.True(info.IsSupported);
        Assert.Contains("archaic iNES", info.CompatibilityWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BatterySaveWithTheWrongSizeIsRejected()
    {
        using var image = TemporaryNesImage.Create(mapper: 1, battery: true);
        var savePath = Path.Combine(image.DirectoryPath, "game.sav");
        File.WriteAllBytes(savePath, new byte[16]);

        var exception = Assert.Throws<InvalidDataException>(() => Cartridge.Load(image.Path, savePath));

        Assert.Contains("expects 8192 bytes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OamDmaWriteQueuesTheSourcePageForTheCycleScheduler()
    {
        using var image = TemporaryNesImage.Create(mapper: 0);
        var cartridge = Cartridge.Load(image.Path);
        var bus = new NesBus(cartridge);

        bus.Write(0x4014, 0x02);

        Assert.True(bus.TryTakeOamDma(out var page));
        Assert.Equal(0x02, page);
        Assert.False(bus.TryTakeOamDma(out _));
    }

    [Fact]
    public void CpuIoReadsPreserveTheExternalOpenBusPins()
    {
        using var image = TemporaryNesImage.Create(mapper: 0);
        var bus = new NesBus(Cartridge.Load(image.Path));
        bus.SetControllerState(1, NesButton.A);
        bus.Write(0x4016, 0x01);
        bus.Write(0x4016, 0x00);

        bus.Write(0x0000, 0xA0);

        Assert.Equal(0xA0, bus.Read(0x4000));
        Assert.Equal(0xA1, bus.Read(0x4016));
    }

    [Fact]
    public void ApuStatusUsesBitFiveWithoutReplacingTheExternalOpenBus()
    {
        using var image = TemporaryNesImage.Create(mapper: 0);
        var bus = new NesBus(Cartridge.Load(image.Path));
        bus.Write(0x0000, 0xA0);

        Assert.Equal(0x20, bus.Read(0x4015));
        Assert.Equal(0xA0, bus.Read(0x4000));
    }

    [Fact]
    public void ConsecutiveDmaControllerSelectDoesNotDeleteAnotherButton()
    {
        using var image = TemporaryNesImage.Create(mapper: 0);
        var bus = new NesBus(Cartridge.Load(image.Path));
        bus.SetControllerState(1, NesButton.B);
        bus.Write(0x4016, 0x01);
        bus.Write(0x4016, 0x00);

        Assert.Equal(0, bus.Read(0x4016) & 1);
        var previousReadAddress = (ushort)0x4016;
        _ = bus.ReadForDma(
            0xC016,
            enableInternalIoReads: true,
            ref previousReadAddress);

        Assert.Equal(1, bus.Read(0x4016) & 1);
    }

    [Fact]
    public void DmaInternalApuStatusReadClearsTheFrameIrq()
    {
        using var image = TemporaryNesImage.Create(mapper: 0);
        var bus = new NesBus(Cartridge.Load(image.Path));
        bus.Apu.Clock(29_827);
        Assert.True(bus.Apu.IrqPending);

        var previousReadAddress = (ushort)0x4014;
        _ = bus.ReadForDma(
            0xC015,
            enableInternalIoReads: true,
            ref previousReadAddress);

        Assert.False(bus.Apu.IrqPending);
    }

    [Fact]
    public void InvalidSaveStateRollsBackWithoutChangingTheRunningMachine()
    {
        using var image = TemporaryNesImage.Create(mapper: 0);
        var machine = NesMachine.Load(image.Path);
        for (var frame = 0; frame < 10; frame++)
        {
            machine.RunFrame();
        }

        var stableState = machine.SaveState();
        var expectedNextFrame = machine.RunFrame().ToArray();
        machine.LoadState(stableState);
        var truncatedState = stableState[..(stableState.Length / 2)];

        Assert.Throws<InvalidDataException>(() => machine.LoadState(truncatedState));
        Assert.Equal(expectedNextFrame, machine.RunFrame().ToArray());
    }

    [Fact]
    public void CorruptSaveStateFailsIntegrityBeforeChangingTheRunningMachine()
    {
        using var image = TemporaryNesImage.Create(mapper: 0);
        var machine = NesMachine.Load(image.Path);
        for (var frame = 0; frame < 10; frame++)
        {
            machine.RunFrame();
        }

        var stableState = machine.SaveState();
        var expectedNextFrame = machine.RunFrame().ToArray();
        machine.LoadState(stableState);
        var corruptState = stableState.ToArray();
        corruptState[corruptState.Length / 2] ^= 0x80;

        var exception = Assert.Throws<InvalidDataException>(() => machine.LoadState(corruptState));

        Assert.Contains("integrity", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expectedNextFrame, machine.RunFrame().ToArray());
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
            bool battery = false,
            bool trainer = false,
            bool nes20 = false,
            int prgRamShift = 0,
            int prgNvRamShift = 0,
            int chrRamShift = 0,
            NesTimingMode timing = NesTimingMode.Ntsc,
            byte defaultInputDevice = 0,
            bool exponentPrgSize = false,
            byte consoleType = 0,
            int prgBanks = 1,
            int chrBanks = 0)
        {
            var directoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PixelDeck.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var path = System.IO.Path.Combine(directoryPath, "game.nes");

            var prgLength = exponentPrgSize ? 16_384 : prgBanks * 16_384;
            var chrLength = chrBanks * 8_192;
            var trainerLength = trainer ? 512 : 0;
            var image = new byte[16 + trainerLength + prgLength + chrLength];
            image[0] = (byte)'N';
            image[1] = (byte)'E';
            image[2] = (byte)'S';
            image[3] = 0x1A;
            image[4] = exponentPrgSize ? (byte)(14 << 2) : (byte)prgBanks;
            image[5] = (byte)chrBanks;
            image[6] = (byte)(((mapper & 0x0F) << 4) | (battery ? 0x02 : 0) | (trainer ? 0x04 : 0));
            image[7] = (byte)((mapper & 0xF0) | (nes20 ? 0x08 : 0) | (consoleType & 0x03));

            if (nes20)
            {
                image[8] = (byte)((mapper >> 8) & 0x0F);
                image[9] = exponentPrgSize ? (byte)0x0F : (byte)0;
                image[10] = (byte)((prgNvRamShift << 4) | prgRamShift);
                image[11] = (byte)chrRamShift;
                image[12] = timing switch
                {
                    NesTimingMode.Pal => (byte)1,
                    NesTimingMode.MultipleRegion => (byte)2,
                    NesTimingMode.Dendy => (byte)3,
                    _ => (byte)0
                };
                image[15] = defaultInputDevice;
            }

            var resetVector = 16 + trainerLength + 0x3FFC;
            image[resetVector] = 0x00;
            image[resetVector + 1] = 0x80;
            File.WriteAllBytes(path, image);
            return new TemporaryNesImage(directoryPath, path);
        }

        public void Dispose()
        {
            var testParent = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PixelDeck.Tests"))
                .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var resolvedRoot = System.IO.Path.GetFullPath(DirectoryPath);
            if (!resolvedRoot.StartsWith(testParent, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to remove a directory outside the PixelDeck test area.");
            }

            if (Directory.Exists(resolvedRoot))
            {
                Directory.Delete(resolvedRoot, recursive: true);
            }
        }
    }
}
