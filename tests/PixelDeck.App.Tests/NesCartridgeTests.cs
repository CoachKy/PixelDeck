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
            bool exponentPrgSize = false)
        {
            var directoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PixelDeck.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var path = System.IO.Path.Combine(directoryPath, "game.nes");

            var prgLength = 16_384;
            var trainerLength = trainer ? 512 : 0;
            var image = new byte[16 + trainerLength + prgLength];
            image[0] = (byte)'N';
            image[1] = (byte)'E';
            image[2] = (byte)'S';
            image[3] = 0x1A;
            image[4] = exponentPrgSize ? (byte)(14 << 2) : (byte)1;
            image[6] = (byte)(((mapper & 0x0F) << 4) | (battery ? 0x02 : 0) | (trainer ? 0x04 : 0));
            image[7] = (byte)((mapper & 0xF0) | (nes20 ? 0x08 : 0));

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
