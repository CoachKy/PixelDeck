using PixelDeck.Emulation.Nes;

namespace PixelDeck.App.Tests;

public sealed class Cpu6502Tests
{
    [Theory]
    [InlineData(false, 513)]
    [InlineData(true, 514)]
    public void OamDmaRunsAsScheduledReadWritePairsWithParityAlignment(
        bool oddWriteCycle,
        int expectedDmaCycles)
    {
        var prefix = oddWriteCycle
            ? new byte[] { 0x24, 0x00 } // BIT $00: shift the write by three cycles
            : [];
        var program = prefix.Concat(new byte[]
        {
            0xA9, 0x02,             // LDA #$02
            0x8D, 0x14, 0x40,       // STA $4014
            0x4C, 0x05, 0x80        // JMP
        }).ToArray();
        using var image = TestNesImage.Create(program);
        var bus = new NesBus(Cartridge.Load(image.Path));
        bus.Write(0x0200, 0x3C);
        bus.Write(0x02FF, 0xA5);
        var cpu = new Cpu6502(bus);
        cpu.Reset();

        if (oddWriteCycle)
        {
            Assert.Equal(3, cpu.Step());
        }
        Assert.Equal(2, cpu.Step());
        Assert.Equal(4, cpu.Step());
        var dmaStepCycles = cpu.Step();

        Assert.Equal(3 + expectedDmaCycles, dmaStepCycles);
        bus.Ppu.CpuWriteRegister(3, 0x00);
        Assert.Equal(0x3C, bus.Ppu.CpuReadRegister(4));
        bus.Ppu.CpuWriteRegister(3, 0xFF);
        Assert.Equal(0xA5, bus.Ppu.CpuReadRegister(4));
    }

    [Fact]
    public void DmcFetchStallAdvancesTheSharedScheduler()
    {
        using var image = TestNesImage.Create(
        [
            0xA9, 0x00,             // LDA #$00
            0x8D, 0x10, 0x40,       // STA $4010
            0x8D, 0x12, 0x40,       // STA $4012
            0x8D, 0x13, 0x40,       // STA $4013
            0xA9, 0x10,             // LDA #$10
            0x8D, 0x15, 0x40,       // STA $4015
            0x4C, 0x10, 0x80        // JMP
        ]);
        var bus = new NesBus(Cartridge.Load(image.Path));
        var cpu = new Cpu6502(bus);
        cpu.Reset();
        cpu.Step();
        cpu.Step();
        cpu.Step();
        cpu.Step();
        cpu.Step();

        Assert.Equal(4, cpu.Step());
        Assert.Equal(7, cpu.Step());
    }

    [Fact]
    public void DmcAndOamDmaArbitrateOnSharedGetAndPutCycles()
    {
        using var image = TestNesImage.Create(
        [
            0xEA,                   // NOP
            0x4C, 0x00, 0x80        // JMP $8000
        ]);
        var bus = new NesBus(Cartridge.Load(image.Path));
        var cpu = new Cpu6502(bus);
        cpu.Reset();
        bus.Write(0x0200, 0x3C);
        bus.Write(0x02FF, 0xA5);
        bus.Apu.WriteRegister(0x4010, 0x8F);
        bus.Apu.WriteRegister(0x4012, 0x00);
        bus.Apu.WriteRegister(0x4013, 0x00);
        bus.Apu.WriteRegister(0x4015, 0x10);
        bus.Apu.Clock(1);
        bus.Write(0x4014, 0x02);

        var stepCycles = cpu.Step();

        // OAM alone would consume 513 cycles at this parity. DMC steals one
        // get cycle and forces one matching alignment cycle.
        Assert.Equal(2 + 515, stepCycles);
        Assert.False(bus.Apu.DmcDmaPending);
        Assert.True(bus.Apu.IrqPending);
        bus.Ppu.CpuWriteRegister(3, 0x00);
        Assert.Equal(0x3C, bus.Ppu.CpuReadRegister(4));
        bus.Ppu.CpuWriteRegister(3, 0xFF);
        Assert.Equal(0xA5, bus.Ppu.CpuReadRegister(4));
    }

    [Fact]
    public void IndexedStorePerformsItsObservableDummyRead()
    {
        using var image = TestNesImage.Create(
        [
            0x78,                   // SEI
            0xA0, 0x07,             // LDY #$07
            0xA9, 0x20,             // LDA #$20
            0x8D, 0x06, 0x20,       // STA $2006
            0xA9, 0x00,             // LDA #$00
            0x8D, 0x06, 0x20,       // STA $2006
            0xA9, 0xAA,             // LDA #$AA
            0x99, 0x00, 0x20,       // STA $2000,Y (dummy read from $2007)
            0xA9, 0x20,             // LDA #$20
            0x8D, 0x06, 0x20,       // STA $2006
            0xA9, 0x00,             // LDA #$00
            0x8D, 0x06, 0x20,       // STA $2006
            0xAD, 0x07, 0x20,       // LDA $2007
            0x85, 0x00,             // STA $00
            0x4C, 0x21, 0x80        // JMP $8021
        ]);

        var bus = new NesBus(Cartridge.Load(image.Path));
        bus.Ppu.CpuWriteRegister(6, 0x20);
        bus.Ppu.CpuWriteRegister(6, 0x00);
        bus.Ppu.CpuWriteRegister(7, 0x11);
        var cpu = new Cpu6502(bus);
        cpu.Reset();

        for (var instruction = 0; instruction < 14; instruction++)
        {
            cpu.Step();
        }

        Assert.Equal(0x11, bus.Read(0));
    }

    private sealed class TestNesImage : IDisposable
    {
        private TestNesImage(string directoryPath, string path)
        {
            DirectoryPath = directoryPath;
            Path = path;
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public static TestNesImage Create(byte[] program)
        {
            var directoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PixelDeck.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            var path = System.IO.Path.Combine(directoryPath, "cpu-dummy-read.nes");
            var image = new byte[16 + 16_384 + 8_192];
            image[0] = (byte)'N';
            image[1] = (byte)'E';
            image[2] = (byte)'S';
            image[3] = 0x1A;
            image[4] = 1;
            image[5] = 1;
            program.CopyTo(image, 16);

            var vectorOffset = 16 + 0x3FFA;
            WriteVector(image, vectorOffset, 0x8000);
            WriteVector(image, vectorOffset + 2, 0x8000);
            WriteVector(image, vectorOffset + 4, 0x8000);
            File.WriteAllBytes(path, image);
            return new TestNesImage(directoryPath, path);
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

        private static void WriteVector(byte[] image, int offset, ushort address)
        {
            image[offset] = (byte)address;
            image[offset + 1] = (byte)(address >> 8);
        }
    }
}
