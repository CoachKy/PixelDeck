using System.Numerics;
using PixelDeck.Emulation.N64;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

public sealed class N64MachineTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(N64ImageByteOrder.BigEndian)]
    [InlineData(N64ImageByteOrder.ByteSwapped)]
    [InlineData(N64ImageByteOrder.LittleEndian)]
    public void CartridgeNormalizesEveryStandardDumpByteOrder(N64ImageByteOrder byteOrder)
    {
        var canonical = N64TestSupport.CreateCartridgeImage();
        var source = N64TestSupport.ConvertByteOrder(canonical, byteOrder);

        var cartridge = N64Cartridge.FromBytes(source);

        Assert.Equal(byteOrder, cartridge.SourceByteOrder);
        Assert.Equal("PIXEL64 TEST", cartridge.Title);
        Assert.Equal("NPXE", cartridge.GameCode);
        Assert.Equal(0x80000400u, cartridge.EntryPoint);
        Assert.True(canonical.AsSpan().SequenceEqual(cartridge.Rom));
    }

    [Fact]
    public void MachineLoadAllowsAnUnverifiedRecognizedCartridgeToAttemptBoot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"Pixel64-{Guid.NewGuid():N}.z64");
        try
        {
            File.WriteAllBytes(path, N64TestSupport.CreateCartridgeImage());

            var machine = N64Machine.Load(path);

            Assert.Equal("PIXEL64 TEST", machine.Cartridge.Title);
            Assert.False(machine.Cartridge.IsPixel64VerifiedTarget);
            Assert.Equal(N64Cic.Unknown, machine.Cartridge.Cic);
            machine.RunInstructions(1);
            Assert.Equal(1, machine.Cpu.InstructionsExecuted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EveryLocalNintendo64CartridgeIsRecognizedAndCanAttemptBoot()
    {
        foreach (var path in N64TestSupport.FindCartridges())
        {
            // Inspect throws on an unrecognized header, so reaching here is
            // the recognition assertion; the header must also be readable.
            var cartridge = N64Cartridge.Inspect(path);

            Assert.Equal(4, cartridge.GameCode.Length);
            Assert.True(cartridge.SaveSize > 0);
            output.WriteLine(
                $"{Path.GetFileName(path)}: {cartridge.Title}, {cartridge.GameCode}, " +
                $"{cartridge.Cic}, {cartridge.SourceByteOrder}");
        }
    }

    [Theory]
    // Verified against the real cartridges: SM64/Quest 64/Ocarina program a
    // 320-wide frame buffer, GoldenEye 007 programs 440.
    [InlineData(320u, 0x400u, 320, 237)]
    [InlineData(440u, 0x580u, 440, 325)]
    [InlineData(640u, 0x400u, 640, 237)]
    public void VideoResolutionFollowsTheVideoInterfaceRegisters(
        uint viWidth,
        uint verticalScale,
        int expectedWidth,
        int expectedHeight)
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0xA4400008, viWidth);
        machine.Memory.WriteUInt32(0xA4400028, (37u << 16) | 511u);
        machine.Memory.WriteUInt32(0xA4400034, verticalScale);

        Assert.Equal(expectedWidth, machine.Width);
        Assert.Equal(expectedHeight, machine.Height);
        Assert.Equal(expectedWidth * expectedHeight, machine.CurrentFrame.Length);
    }

    [Fact]
    public void VideoResolutionKeepsTheLastValidSizeWhileTheInterfaceIsUnprogrammed()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));

        machine.Memory.WriteUInt32(0xA4400008, 0);

        Assert.Equal(320, machine.Width);
        Assert.Equal(240, machine.Height);
    }

    [Theory]
    [InlineData("NZLE", N64SaveType.Sram256Kbit, 32 * 1024, ".sra")]
    [InlineData("NGEE", N64SaveType.Eeprom16Kbit, 2 * 1024, ".eep")]
    [InlineData("NPXE", N64SaveType.Eeprom4Kbit, 512, ".eep")]
    public void CartridgeDeclaresItsBatteryStoreRatherThanInferringItFromFileLength(
        string gameCode,
        N64SaveType expectedType,
        int expectedSize,
        string expectedExtension)
    {
        var image = N64TestSupport.CreateCartridgeImage();
        image[0x3B] = (byte)gameCode[0];
        image[0x3C] = (byte)gameCode[1];
        image[0x3D] = (byte)gameCode[2];
        image[0x3E] = (byte)gameCode[3];

        var cartridge = N64Cartridge.FromBytes(image);

        Assert.Equal(gameCode, cartridge.GameCode);
        Assert.Equal(expectedType, cartridge.SaveType);
        Assert.Equal(expectedSize, cartridge.SaveSize);
        Assert.Equal(expectedExtension, cartridge.SaveExtension);
    }

    [Fact]
    public void CartridgeSramDmaTransfersBothWaysAndAlwaysSignalsCompletion()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.Rdram[0x200] = 0xAB;
        memory.Rdram[0x201] = 0xCD;

        // RDRAM -> SRAM at cartridge address 0x08000000.
        memory.WriteUInt32(0xA4600000, 0x00000200);
        memory.WriteUInt32(0xA4600004, 0x08000000);
        memory.WriteUInt32(0xA4600008, 1);

        Assert.Equal(0xAB, memory.Sram[0]);
        Assert.Equal(0xCD, memory.Sram[1]);
        Assert.True(memory.SramDirty);
        Assert.NotEqual(0u, memory.MiInterrupt & (1u << 4));

        // SRAM -> RDRAM must also raise the completion interrupt, because
        // libultra blocks on the DMA message queue.
        memory.WriteUInt32(0xA4600010, 2);
        memory.WriteUInt32(0xA4600000, 0x00000300);
        memory.WriteUInt32(0xA4600004, 0x08000000);
        memory.WriteUInt32(0xA460000C, 1);

        Assert.Equal(0xAB, memory.Rdram[0x300]);
        Assert.Equal(0xCD, memory.Rdram[0x301]);
        Assert.NotEqual(0u, memory.MiInterrupt & (1u << 4));
    }

    [Fact]
    public void UnbackedCartridgeDmaStillSignalsCompletion()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));

        // A domain the cartridge does not provide at all must not deadlock.
        memory.WriteUInt32(0xA4600000, 0x00000400);
        memory.WriteUInt32(0xA4600004, 0x05000000);
        memory.WriteUInt32(0xA460000C, 16);

        Assert.NotEqual(0u, memory.MiInterrupt & (1u << 4));
    }

    [Fact]
    public void ControllerSerializesButtonsAndSignedAnalogStick()
    {
        var controller = new N64ControllerState(
            N64Button.A | N64Button.Start | N64Button.CRight,
            StickX: -80,
            StickY: 72);

        Assert.Equal(0x9001B048u, controller.ToPifWord());
    }

    [Fact]
    public void DelaySlotExceptionRecordsTheBranchEpcAndCauseBdBit()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0xA4000040, 0x10000002);
        machine.Memory.WriteUInt32(0xA4000044, 0x0000000C);

        machine.RunInstructions(2);

        Assert.Equal(0x80000180u, machine.Cpu.ProgramCounter);
        Assert.Equal(0xA4000040u, machine.Cpu.ReadCoprocessor0(14));
        Assert.Equal(0x80000020u, machine.Cpu.ReadCoprocessor0(13) & 0x8000007C);
    }

    [Fact]
    public void SinglePrecisionArithmeticAndTruncateWordKeepTheCorrectFprFormat()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0xA4000040, 0x3C083FC0);
        machine.Memory.WriteUInt32(0xA4000044, 0x44880000);
        machine.Memory.WriteUInt32(0xA4000048, 0x46000080);
        machine.Memory.WriteUInt32(0xA400004C, 0x4600110D);
        machine.Memory.WriteUInt32(0xA4000050, 0x44092000);

        machine.RunInstructions(5);

        Assert.Equal(3u, (uint)machine.Cpu.Registers[9]);
    }

    [Fact]
    public void DoublePrecisionUsesEvenOddRegisterPairsWhenStatusFrIsClear()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0xA4000040, 0x40806000);
        machine.Memory.WriteUInt32(0xA4000044, 0x3C083FF8);
        machine.Memory.WriteUInt32(0xA4000048, 0x44800000);
        machine.Memory.WriteUInt32(0xA400004C, 0x44880800);
        machine.Memory.WriteUInt32(0xA4000050, 0x46200080);
        machine.Memory.WriteUInt32(0xA4000054, 0x44291000);

        machine.RunInstructions(6);

        Assert.Equal(0x4008000000000000ul, machine.Cpu.Registers[9]);
    }

    [Fact]
    public void ViCurrentAdvancesWithinAFieldAndInterruptsOnlyOncePerCompareLine()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt32(0xA4400018, 9);
        memory.WriteUInt32(0xA440000C, 4);
        var ticksPerLine = memory.CpuTicksPerField / 10;

        memory.AdvanceCpuTicks(ticksPerLine * 4);

        Assert.Equal(4u, memory.ViCurrent);
        Assert.NotEqual(0u, memory.MiInterrupt & (1u << 3));

        memory.WriteUInt32(0xA4400010, 0);
        memory.AdvanceCpuTicks(1);

        Assert.Equal(0u, memory.MiInterrupt & (1u << 3));

        memory.AdvanceCpuTicks(
            memory.CpuTicksPerField - (ticksPerLine * 4) - 1 + (ticksPerLine * 4));

        Assert.Equal(4u, memory.ViCurrent);
        Assert.NotEqual(0u, memory.MiInterrupt & (1u << 3));
    }

    [Fact]
    public void AudioInterfaceQueuesTwoDmasAndInterruptsAsEachCompletes()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt32(0xA4500010, 1519);
        memory.WriteUInt32(0xA4500008, 1);
        memory.WriteUInt32(0xA4500000, 0x1000);
        memory.WriteUInt32(0xA4500004, 0x1000);
        memory.WriteUInt32(0xA4500000, 0x2000);
        memory.WriteUInt32(0xA4500004, 0x0800);

        Assert.Equal(0xC0000000u, memory.ReadUInt32(0xA450000C));
        Assert.InRange(memory.ReadUInt32(0xA4500004), 1u, 0x1000u);

        memory.AdvanceCpuTicks(memory.CpuTicksPerField * 4);

        Assert.Equal(2, memory.AudioDmasCompleted);
        Assert.Equal(0u, memory.ReadUInt32(0xA450000C));
        Assert.NotEqual(0u, memory.MiInterrupt & (1u << 2));

        memory.WriteUInt32(0xA450000C, 0);

        Assert.Equal(0u, memory.MiInterrupt & (1u << 2));
    }

    [Fact]
    public void PiDmaCopiesBigEndianCartridgeBytesIntoRdram()
    {
        var image = N64TestSupport.CreateCartridgeImage();
        image[0x1000] = 0x12;
        image[0x1001] = 0x34;
        image[0x1002] = 0x56;
        image[0x1003] = 0x78;
        var memory = new N64Memory(N64Cartridge.FromBytes(image));

        memory.WriteUInt32(0xA4600000, 0x00000100);
        memory.WriteUInt32(0xA4600004, 0x10001000);
        memory.WriteUInt32(0xA460000C, 3);

        Assert.Equal(0x12345678u, memory.ReadUInt32(0x80000100));
        Assert.NotEqual(0u, memory.MiInterrupt & (1u << 4));
    }

    [Fact]
    public void TlbMapsPairedSixtyFourKilobytePagesIntoUserVirtualMemory()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt32(0x803B0668, 0x0000D1D4);
        memory.WriteUInt32(0x803C0668, 0x12345678);
        memory.WriteTlbEntry(
            index: 0,
            pageMask: 0x001E000,
            entryHi: 0x04000000,
            entryLo0: (0x003B0000 >> 6) | 31u,
            entryLo1: (0x003C0000 >> 6) | 31u);

        Assert.Equal(0x0000D1D4u, memory.ReadUInt32(0x04000668));
        Assert.Equal(0x12345678u, memory.ReadUInt32(0x04010668));
    }

    [Fact]
    public void SiPifReturnsPortOneButtonsAndAnalogStick()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.SetControllerState(
            1,
            new N64ControllerState(N64Button.A | N64Button.Z, -60, 42));
        memory.Rdram[0x100] = 1;
        memory.Rdram[0x101] = 4;
        memory.Rdram[0x102] = 1;
        memory.Rdram[0x107] = 0xFE;
        memory.Rdram[0x13F] = 1;

        memory.WriteUInt32(0xA4800000, 0x100);
        memory.WriteUInt32(0xA4800010, 0x1FC007C0);
        memory.AdvanceCpuTicks(256);
        memory.WriteUInt32(0xA4800004, 0x1FC007C0);
        memory.AdvanceCpuTicks(256);

        Assert.Equal(0xA000C42Au, memory.ReadUInt32(0x80000103));
        Assert.NotEqual(0u, memory.MiInterrupt & (1u << 1));

        memory.SetControllerState(1, new N64ControllerState(N64Button.Start, 0, 0));
        memory.WriteUInt32(0xA4800004, 0x1FC007C0);
        memory.AdvanceCpuTicks(256);

        Assert.Equal(0x10000000u, memory.ReadUInt32(0x80000103));
        Assert.True(memory.ControllerPolls >= 2);
    }

    [Fact]
    public void SiPifReportsEmptyControllerPortsAsAbsentUntilAPadIsPluggedIn()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.SetControllerConnected(2, false);

        // A leading zero byte advances the PIF to channel 1, i.e. controller port two, which then
        // gets a status probe: one transmit byte (command 0x00) and three receive bytes.
        memory.Rdram[0x100] = 0;
        memory.Rdram[0x101] = 1;
        memory.Rdram[0x102] = 3;
        memory.Rdram[0x103] = 0;
        memory.Rdram[0x107] = 0xFE;
        memory.Rdram[0x13F] = 1;

        memory.WriteUInt32(0xA4800000, 0x100);
        memory.WriteUInt32(0xA4800010, 0x1FC007C0);
        memory.AdvanceCpuTicks(256);
        memory.WriteUInt32(0xA4800004, 0x1FC007C0);
        memory.AdvanceCpuTicks(256);

        // Bit 7 of the receive descriptor is the PIF's "no device on this channel" answer.
        Assert.False(memory.IsControllerConnected(2));
        Assert.Equal(0x80, memory.Rdram[0x102] & 0x80);
        Assert.Equal(0, memory.Rdram[0x104]);

        memory.SetControllerConnected(2, true);
        memory.Rdram[0x102] = 3;
        memory.Rdram[0x104] = 0;
        memory.WriteUInt32(0xA4800010, 0x1FC007C0);
        memory.AdvanceCpuTicks(256);
        memory.WriteUInt32(0xA4800004, 0x1FC007C0);
        memory.AdvanceCpuTicks(256);

        Assert.Equal(0, memory.Rdram[0x102] & 0x80);
        Assert.Equal(0x05, memory.Rdram[0x104]);
    }

    [Fact]
    public void DisconnectingAControllerPortAlsoClearsItsHeldButtons()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        machine.SetControllerConnected(3, true);
        machine.SetControllerState(3, new N64ControllerState(N64Button.A | N64Button.Start, 40, -40));

        machine.SetControllerConnected(3, false);

        Assert.False(machine.IsControllerConnected(3));
        Assert.Equal(N64ControllerState.Neutral, machine.GetControllerState(3));
    }

    [Fact]
    public void EveryPortReportsOccupiedByDefault()
    {
        // Reporting empty ports as absent is correct hardware behaviour and SetControllerConnected
        // implements it, but Super Mario 64 stalls when ports answer "no device", so the default
        // stays permissive. Locking that in here so the default is not changed by accident.
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));

        for (var port = 1; port <= 4; port++)
        {
            Assert.True(machine.IsControllerConnected(port));
        }
    }

    [Fact]
    public void SaveStateRestoresCpuMemoryVideoAndBothControllerPortsExactly()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0x80000100, 0x12345678);
        machine.Memory.WriteUInt32(0xA4400000, 2);
        machine.Memory.WriteUInt32(0xA4400004, 0x100);
        machine.Memory.WriteUInt32(0xA4400008, 1);
        machine.SetControllerState(1, new(N64Button.A | N64Button.Z, 50, -20));
        machine.SetControllerState(2, new(N64Button.B | N64Button.Start, -40, 10));
        var state = machine.SaveState();

        machine.Memory.WriteUInt32(0x80000100, 0);
        machine.SetControllerState(1, default);
        machine.SetControllerState(2, default);
        machine.LoadState(state);

        Assert.Equal(0x12345678u, machine.Memory.ReadUInt32(0x80000100));
        Assert.Equal(new N64ControllerState(N64Button.A | N64Button.Z, 50, -20), machine.GetControllerState(1));
        Assert.Equal(new N64ControllerState(N64Button.B | N64Button.Start, -40, 10), machine.GetControllerState(2));
    }

    [Fact]
    public void SpTaskSchedulerRecognizesAndCompletesGraphicsTasks()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0xA4000FC0, 1);
        machine.Memory.WriteUInt32(0xA4000FF0, 0x00123456);
        machine.Memory.WriteUInt32(0xA4000FF4, 0x00000400);
        machine.Memory.WriteUInt32(0xA4000FF8, 0x00654321);
        machine.Memory.WriteUInt32(0xA4000FFC, 0x00000800);
        machine.Memory.WriteUInt32(0xA4040010, 1);

        machine.RunInstructions(1);

        Assert.Equal(1, machine.GraphicsTasksSubmitted);
        Assert.Equal(0x00123456u, machine.LastRspTask?.DataPointer);
        Assert.Equal(0x00000400u, machine.LastRspTask?.DataSize);
        Assert.Equal(0x00654321u, machine.LastRspTask?.YieldDataPointer);
        Assert.Equal(0x00000800u, machine.LastRspTask?.YieldDataSize);
        Assert.Equal(3u, machine.Memory.ReadUInt32(0xA4040010) & 3);
        Assert.NotEqual(0u, machine.Memory.ReadUInt32(0xA4040010) & (1u << 9));
        Assert.NotEqual(0u, machine.Memory.MiInterrupt & (1u << 5));
    }

    [Fact]
    public void Fast3dFillRectangleWritesTheSelectedRgba16ColorImage()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0xA4000FC0, 1);
        machine.Memory.WriteUInt32(0xA4000FF0, 0x00001000);
        machine.Memory.WriteUInt32(0xA4000FF4, 32);
        machine.Memory.WriteUInt32(0x80001000, 0xFF100003);
        machine.Memory.WriteUInt32(0x80001004, 0x00002000);
        machine.Memory.WriteUInt32(0x80001008, 0xF7000000);
        machine.Memory.WriteUInt32(0x8000100C, 0x7C1F7C1F);
        machine.Memory.WriteUInt32(0x80001010, 0xF600C00C);
        machine.Memory.WriteUInt32(0x80001014, 0x00000000);
        machine.Memory.WriteUInt32(0x80001018, 0xB8000000);
        machine.Memory.WriteUInt32(0x8000101C, 0);
        machine.Memory.WriteUInt32(0xA4040010, 1);

        machine.RunInstructions(1);

        Assert.Equal(0x7C1Fu, machine.Memory.ReadUInt16(0x80002000));
        Assert.Equal(0x7C1Fu, machine.Memory.ReadUInt16(0x8000201E));
        Assert.Equal(1, machine.Renderer.FillRectanglesDrawn);
    }

    [Fact]
    public void Fast3dTextureRectangleSamplesAnRgba16Texture()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt16(0x100, 0xF801);
        memory.WriteUInt16(0x102, 0x07C1);
        memory.WriteUInt16(0x104, 0x003F);
        memory.WriteUInt16(0x106, 0xFFFF);
        memory.WriteUInt32(0x200, 0xFD100000);
        memory.WriteUInt32(0x204, 0x00000100);
        memory.WriteUInt32(0x208, 0xF5100200);
        memory.WriteUInt32(0x20C, 0);
        memory.WriteUInt32(0x210, 0xF3000000);
        memory.WriteUInt32(0x214, 0x00003000);
        memory.WriteUInt32(0x218, 0xF2000000);
        memory.WriteUInt32(0x21C, 0x00004004);
        memory.WriteUInt32(0x220, 0xFF100003);
        memory.WriteUInt32(0x224, 0x00000400);
        memory.WriteUInt32(0x228, 0xE4008008);
        memory.WriteUInt32(0x22C, 0);
        memory.WriteUInt32(0x230, 0xB3000000);
        memory.WriteUInt32(0x234, 0);
        memory.WriteUInt32(0x238, 0xB2000000);
        memory.WriteUInt32(0x23C, 0x04000400);
        memory.WriteUInt32(0x240, 0xB8000000);
        memory.WriteUInt32(0x244, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 72, 0, 0));

        Assert.Equal(0xF801, memory.ReadUInt16(0x400));
        Assert.Equal(0x07C1, memory.ReadUInt16(0x402));
        Assert.Equal(0x003F, memory.ReadUInt16(0x408));
        Assert.Equal(0xFFFF, memory.ReadUInt16(0x40A));
        Assert.Equal(1, renderer.TextureRectanglesDrawn);
    }

    [Fact]
    public void Fast3dTextureLoadCopiesRdramIntoPersistentTmem()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt16(0x100, 0xF801);
        memory.WriteUInt16(0x102, 0x07C1);
        memory.WriteUInt16(0x104, 0x003F);
        memory.WriteUInt16(0x106, 0xFFFF);
        memory.WriteUInt32(0x200, 0xFD100000);
        memory.WriteUInt32(0x204, 0x00000100);
        memory.WriteUInt32(0x208, 0xF5100000);
        memory.WriteUInt32(0x20C, 0x07000000);
        memory.WriteUInt32(0x210, 0xF3000000);
        memory.WriteUInt32(0x214, 0x07003000);
        memory.WriteUInt32(0x218, 0xB8000000);
        memory.WriteUInt32(0x21C, 0);
        memory.WriteUInt32(0x300, 0xF5100200);
        memory.WriteUInt32(0x304, 0);
        memory.WriteUInt32(0x308, 0xF2000000);
        memory.WriteUInt32(0x30C, 0x00004004);
        memory.WriteUInt32(0x310, 0xFF100003);
        memory.WriteUInt32(0x314, 0x00000400);
        memory.WriteUInt32(0x318, 0xE4008008);
        memory.WriteUInt32(0x31C, 0);
        memory.WriteUInt32(0x320, 0xB3000000);
        memory.WriteUInt32(0x324, 0);
        memory.WriteUInt32(0x328, 0xB2000000);
        memory.WriteUInt32(0x32C, 0x04000400);
        memory.WriteUInt32(0x330, 0xB8000000);
        memory.WriteUInt32(0x334, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 32, 0, 0));
        memory.WriteUInt32(0x100, 0);
        memory.WriteUInt32(0x104, 0);
        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x300, 56, 0, 0));

        Assert.Equal(0xF801, memory.ReadUInt16(0x400));
        Assert.Equal(0x07C1, memory.ReadUInt16(0x402));
        Assert.Equal(0x003F, memory.ReadUInt16(0x408));
        Assert.Equal(0xFFFF, memory.ReadUInt16(0x40A));
        Assert.Equal(1, renderer.TextureRectanglesDrawn);
    }

    [Fact]
    public void Fast3dCombinerDetectsWhetherAColorCycleUsesTexture()
    {
        Assert.False(Fast3dRenderer.CombineUsesTexture(0xFCFFFFFF, 0xFFFE793C));
        Assert.True(Fast3dRenderer.CombineUsesTexture(0xFC100000, 0));
    }

    [Fact]
    public void Fast3dClipFlagsIdentifyVerticesOutsideEachHomogeneousPlane()
    {
        Assert.Equal(0, Fast3dRenderer.ComputeClipFlags(new Vector4(0, 0, 0, 1)));
        Assert.Equal(1 << 0, Fast3dRenderer.ComputeClipFlags(new Vector4(-2, 0, 0, 1)));
        Assert.Equal(1 << 1, Fast3dRenderer.ComputeClipFlags(new Vector4(2, 0, 0, 1)));
        Assert.Equal(1 << 2, Fast3dRenderer.ComputeClipFlags(new Vector4(0, -2, 0, 1)));
        Assert.Equal(1 << 3, Fast3dRenderer.ComputeClipFlags(new Vector4(0, 2, 0, 1)));
        Assert.Equal(1 << 4, Fast3dRenderer.ComputeClipFlags(new Vector4(0, 0, -2, 1)));
        Assert.Equal(1 << 5, Fast3dRenderer.ComputeClipFlags(new Vector4(0, 0, 2, 1)));
    }

    [Fact]
    public void Fast3dViewportMapsPositiveClipYTowardTheTopOfTheFramebuffer()
    {
        var scale = new Vector4(160, 120, 511, 0);
        var translate = new Vector4(160, 120, 0, 0);

        var top = Fast3dRenderer.ProjectClipToScreen(
            new Vector4(0, 1, 0, 1),
            1,
            scale,
            translate);
        var bottom = Fast3dRenderer.ProjectClipToScreen(
            new Vector4(0, -1, 0, 1),
            1,
            scale,
            translate);

        Assert.Equal(new Vector3(160, 0, 0), top);
        Assert.Equal(new Vector3(160, 240, 0), bottom);
    }

    [Fact]
    public void Fast3dCullingUsesTopLeftFramebufferWinding()
    {
        const uint cullFront = 0x00001000;
        const uint cullBack = 0x00002000;

        Assert.True(Fast3dRenderer.ShouldCullTriangle(cullFront, 1));
        Assert.False(Fast3dRenderer.ShouldCullTriangle(cullFront, -1));
        Assert.True(Fast3dRenderer.ShouldCullTriangle(cullBack, -1));
        Assert.False(Fast3dRenderer.ShouldCullTriangle(cullBack, 1));
        Assert.False(Fast3dRenderer.ShouldCullTriangle(0, 1));
    }

    [Fact]
    public void Fast3dSetOtherModeLowPreservesUnchangedBitsAndTracksDepthModes()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt32(0x200, 0xB900031D);
        memory.WriteUInt32(0x204, 0x00000030);
        memory.WriteUInt32(0x208, 0xB9000002);
        memory.WriteUInt32(0x20C, 0x00000001);
        memory.WriteUInt32(0x210, 0xB8000000);
        memory.WriteUInt32(0x214, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 24, 0, 0));

        Assert.Equal(0x31u, renderer.OtherModeLow & 0x3Fu);
    }

    [Fact]
    public void MiModeClearDpCommandAcknowledgesTheDisplayProcessorInterrupt()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.CompleteDisplayProcessor();

        Assert.NotEqual(0u, memory.MiInterrupt & (1u << 5));

        memory.WriteUInt32(0xA4300000, 1u << 11);

        Assert.Equal(0u, memory.MiInterrupt & (1u << 5));
    }

    [Fact]
    public void LocalSuperMario64ReachesRenderedCastleGameplayWhenPresent()
    {
        var path = N64TestSupport.FindSuperMario64();
        if (path is null)
        {
            output.WriteLine("Local Super Mario 64 target is not installed; optional gameplay gate skipped.");
            return;
        }

        var machine = N64Machine.Load(path);
        var reachedGameplay = -1;
        for (var field = 0; field < 4_200 && reachedGameplay < 0; field++)
        {
            // Alternate Start and A to walk the title screen, the file select,
            // and the opening cutscene without depending on exact timings.
            var phase = field % 200;
            machine.SetControllerState(
                1,
                phase switch
                {
                    >= 20 and < 40 => new N64ControllerState(N64Button.Start, 0, 0),
                    >= 120 and < 140 => new N64ControllerState(N64Button.A, 0, 0),
                    _ => N64ControllerState.Neutral
                });
            machine.RunFrame();

            // Course 16 area 1 is the castle grounds.
            if (machine.Memory.ReadUInt16(0x8033BACA) == 1 &&
                machine.Memory.ReadUInt32(0x8033B17C) is not (0 or 0x04001301))
            {
                reachedGameplay = field;
            }
        }

        Assert.True(reachedGameplay > 0, "Super Mario 64 never reached castle-grounds gameplay.");

        // Let the scene settle, then require a genuinely rendered frame.
        for (var field = 0; field < 60; field++)
        {
            machine.SetControllerState(1, N64ControllerState.Neutral);
            machine.RunFrame();
        }

        var frame = machine.CurrentFrame.ToArray();
        var distinctColors = frame.Distinct().Count();
        output.WriteLine(
            $"gameplay at field {reachedGameplay}, colors={distinctColors}, " +
            $"triangles={machine.Renderer.TrianglesDrawn:N0}, " +
            $"unsupported={machine.Renderer.UnsupportedCommands}");

        Assert.Equal(0, machine.Cpu.UnsupportedInstructionCount);
        Assert.Equal(0, machine.Renderer.UnsupportedCommands);
        Assert.True(
            machine.Renderer.TrianglesDrawn > 100_000,
            $"Only {machine.Renderer.TrianglesDrawn:N0} triangles were rasterized.");
        Assert.True(
            distinctColors > 500,
            $"The castle-grounds frame only contained {distinctColors} distinct colors.");
    }

    [Fact]
    public void LocalSuperMario64CompletesIpl3WhenPresent()
    {
        var path = N64TestSupport.FindSuperMario64();
        if (path is null)
        {
            output.WriteLine("Local Super Mario 64 target is not installed; optional boot gate skipped.");
            return;
        }

        var cartridge = N64Cartridge.Load(path);
        Assert.True(cartridge.IsSuperMario64UsRevision0);
        Assert.Equal(N64Cic.Cic6102, cartridge.Cic);
        var machine = N64Machine.Create(cartridge);

        for (var index = 0; index < 20_000_000 && !machine.ReachedCartridgeEntryPoint; index++)
        {
            machine.RunInstructions(1);
        }

        output.WriteLine(
            $"PC=0x{machine.Cpu.ProgramCounter:X8}, instructions={machine.Cpu.InstructionsExecuted:N0}, " +
            $"entry=0x{cartridge.EntryPoint:X8}");
        Assert.True(machine.ReachedCartridgeEntryPoint);
        Assert.Equal(0, machine.Cpu.UnsupportedInstructionCount);
    }

    [Fact]
    public void LocalSuperMario64ServicesVideoInterruptsWhenPresent()
    {
        var path = N64TestSupport.FindSuperMario64();
        if (path is null)
        {
            output.WriteLine("Local Super Mario 64 target is not installed; optional VI gate skipped.");
            return;
        }

        var machine = N64Machine.Load(path);
        for (var frame = 0; frame < 120; frame++)
        {
            machine.RunFrame();
        }

        var visibleColors = machine.CurrentFrame.ToArray().Distinct().Count();
        output.WriteLine(
            $"PC=0x{machine.Cpu.ProgramCounter:X8}, frames={machine.FrameNumber}, " +
            $"instructions={machine.Cpu.InstructionsExecuted:N0}, MI=0x{machine.Memory.MiInterrupt:X2}, " +
            $"mask=0x{machine.Memory.MiInterruptMask:X2}, VI=0x{machine.Memory.ViOrigin:X6}/" +
            $"{machine.Memory.ViWidth}, colors={visibleColors}, gfx={machine.GraphicsTasksSubmitted}, " +
            $"audio={machine.AudioTasksSubmitted}, SP=0x{machine.Memory.SpStatus:X4}, " +
            $"VI IRQs={machine.Memory.VerticalInterruptsRaised}, AI DMAs={machine.Memory.AudioDmasCompleted}, " +
            $"last task={machine.LastRspTask}");
        Assert.Equal(0, machine.Cpu.UnsupportedInstructionCount);
        Assert.True(machine.ReachedCartridgeEntryPoint);
        Assert.True(machine.Memory.VerticalInterruptsRaised >= 100);
        Assert.True(machine.AudioTasksSubmitted >= 1);
    }

    [Fact]
    public void TraceLocalSuperMario64PostBootWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PIXEL64_TRACE_BOOT"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var path = N64TestSupport.FindSuperMario64();
        Assert.NotNull(path);
        var machine = N64Machine.Load(path);
        var samples = new Dictionary<uint, int>();
        const int fields = 120;
        const int instructionsPerField = 781_250;
        const int sampleInterval = 1_000;

        for (var field = 0; field < fields; field++)
        {
            var interval = field == 13 ? 1 : sampleInterval;
            var previousRunQueue = machine.Memory.ReadUInt32(0x803359A8);
            var previousRunningThread = machine.Memory.ReadUInt32(0x803359B0);
            var previousMiInterrupt = machine.Memory.MiInterrupt;
            var queueChanges = 0;
            for (var executed = 0; executed < instructionsPerField; executed += interval)
            {
                var instructionAddress = machine.Cpu.ProgramCounter;
                machine.RunInstructions(Math.Min(interval, instructionsPerField - executed));
                samples[machine.Cpu.ProgramCounter] =
                    samples.GetValueOrDefault(machine.Cpu.ProgramCounter) + 1;
                var runQueue = machine.Memory.ReadUInt32(0x803359A8);
                var runningThread = machine.Memory.ReadUInt32(0x803359B0);
                if (field == 13 &&
                    (runQueue != previousRunQueue ||
                     runningThread != previousRunningThread ||
                     machine.Memory.MiInterrupt != previousMiInterrupt) &&
                    queueChanges++ < 40)
                {
                    output.WriteLine(
                        $"queue-change at 0x{instructionAddress:X8}/" +
                        $"0x{machine.Cpu.LastInstruction:X8}: runq 0x{previousRunQueue:X8}->" +
                        $"0x{runQueue:X8}, running 0x{previousRunningThread:X8}->" +
                        $"0x{runningThread:X8}, MI 0x{previousMiInterrupt:X2}->" +
                        $"0x{machine.Memory.MiInterrupt:X2}");
                }

                previousRunQueue = runQueue;
                previousRunningThread = runningThread;
                previousMiInterrupt = machine.Memory.MiInterrupt;
            }

            if (field < 20 || field % 10 == 9)
            {
                output.WriteLine(
                    $"field={field + 1:D3} PC=0x{machine.Cpu.ProgramCounter:X8} " +
                    $"status=0x{machine.Cpu.ReadCoprocessor0(12):X8} " +
                    $"cause=0x{machine.Cpu.ReadCoprocessor0(13):X8} " +
                    $"VI={machine.Memory.ViCurrent}/" +
                    $"{machine.Memory.ReadUInt32(0xA440000C)}/" +
                    $"{machine.Memory.ReadUInt32(0xA4400018)} " +
                    $"MI=0x{machine.Memory.MiInterrupt:X2} " +
                    $"runq=0x{machine.Memory.ReadUInt32(0x803359A8):X8} " +
                    $"p359B0=0x{machine.Memory.ReadUInt32(0x803359B0):X8} " +
                    $"p35A20=0x{machine.Memory.ReadUInt32(0x80335A20):X8}");
                if (field is >= 7 and <= 14)
                {
                    var thread = machine.Memory.ReadUInt32(0x803359A8);
                    for (var queueIndex = 0; queueIndex < 8; queueIndex++)
                    {
                        if (thread is < 0x80000000 or > 0x807FFFFF)
                        {
                            output.WriteLine($"  queue[{queueIndex}]=0x{thread:X8} INVALID");
                            break;
                        }

                        var next = machine.Memory.ReadUInt32(thread);
                        var priority = machine.Memory.ReadUInt32(thread + 4);
                        output.WriteLine(
                            $"  queue[{queueIndex}]=0x{thread:X8} priority={priority} next=0x{next:X8}");
                        thread = next;
                    }
                }
            }
        }

        output.WriteLine(
            $"PC=0x{machine.Cpu.ProgramCounter:X8} SP=0x{machine.Cpu.Registers[29]:X16} " +
            $"RA=0x{machine.Cpu.Registers[31]:X16} status=0x{machine.Cpu.ReadCoprocessor0(12):X8} " +
            $"cause=0x{machine.Cpu.ReadCoprocessor0(13):X8} EPC=0x{machine.Cpu.ReadCoprocessor0(14):X8} " +
            $"MI=0x{machine.Memory.MiInterrupt:X2}/{machine.Memory.MiInterruptMask:X2} " +
            $"VI=0x{machine.Memory.ViOrigin:X8}/{machine.Memory.ViWidth}");
        foreach (var (address, count) in samples.OrderByDescending(pair => pair.Value).Take(24))
        {
            output.WriteLine(
                $"0x{address:X8} x{count:N0} instruction=0x{machine.Memory.ReadUInt32(address):X8}");
        }

        for (var address = 0x803274E0u; address <= 0x80327618; address += 4)
        {
            output.WriteLine($"0x{address:X8}: 0x{machine.Memory.ReadUInt32(address):X8}");
        }

        for (var address = 0x80327C40u; address <= 0x80327D48; address += 4)
        {
            output.WriteLine($"0x{address:X8}: 0x{machine.Memory.ReadUInt32(address):X8}");
        }

        for (var register = 0; register < 32; register++)
        {
            output.WriteLine($"r{register:D2}=0x{machine.Cpu.Registers[register]:X16}");
        }

        for (var address = 0x80335980u; address <= 0x80335A40; address += 4)
        {
            output.WriteLine($"0x{address:X8}: 0x{machine.Memory.ReadUInt32(address):X8}");
        }

        foreach (var thread in new[]
                 {
                     0x8033A730u,
                     0x8033A8E0u,
                     0x8033AA90u,
                     0x8033AC40u,
                     0x80364C60u
                 })
        {
            output.WriteLine(
                $"thread 0x{thread:X8}: next=0x{machine.Memory.ReadUInt32(thread):X8} " +
                $"pri={machine.Memory.ReadUInt32(thread + 4)} " +
                $"state=0x{machine.Memory.ReadUInt32(thread + 0x10):X8} " +
                $"sp=0x{machine.Memory.ReadUInt64(thread + 0xF0):X16} " +
                $"ra=0x{machine.Memory.ReadUInt64(thread + 0x100):X16} " +
                $"status=0x{machine.Memory.ReadUInt32(thread + 0x118):X8} " +
                $"epc=0x{machine.Memory.ReadUInt32(thread + 0x11C):X8}");
        }
    }

    [Fact]
    public void TraceLocalSuperMario64SchedulerWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PIXEL64_TRACE_SCHEDULER"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var path = N64TestSupport.FindSuperMario64();
        Assert.NotNull(path);
        var machine = N64Machine.Load(path);
        uint[]? bestFrame = null;
        var mostNonBlackPixels = -1;
        var bestFrameField = -1;
        var previousGraphicsTasks = 0L;
        var autoplay = string.Equals(
            Environment.GetEnvironmentVariable("PIXEL64_AUTOPLAY"),
            "1",
            StringComparison.Ordinal);
        ushort observedButtonDown = 0;
        ushort observedButtonPressed = 0;
        ushort observedRawPad = 0;
        var marioPositionBeforeMovement = Vector3.Zero;
        var playableField = -1;
        var playableFrames = 0;
        var fieldsRun = 0;
        var controllerTransitions = new List<string>();
        var maximumFields = autoplay ? 6_000 : 600;
        for (var field = 0; field < maximumFields; field++)
        {
            if (autoplay)
            {
                var controllerState =
                    field switch
                    {
                        >= 300 and < 320 => new N64ControllerState(N64Button.Start, 0, 0),
                        >= 480 and < 500 => new N64ControllerState(N64Button.Start, 0, 0),
                        >= 650 and < 670 => new N64ControllerState(N64Button.A, 0, 0),
                        >= 780 and < 800 => new N64ControllerState(N64Button.A, 0, 0),
                        >= 950 and < 970 => new N64ControllerState(N64Button.A, 0, 0),
                        >= 1_100 and < 1_120 => new N64ControllerState(N64Button.A, 0, 0),
                        >= 1_250 and < 1_270 => new N64ControllerState(N64Button.A, 0, 0),
                        >= 1_450 and < 1_470 => new N64ControllerState(N64Button.A, 0, 0),
                        >= 1_600 and < 1_620 => new N64ControllerState(N64Button.A, 0, 0),
                        >= 1_750 and < 1_770 => new N64ControllerState(N64Button.A, 0, 0),
                        >= 2_200 and < 2_220 => new N64ControllerState(N64Button.Start, 0, 0),
                        >= 2_500 and < 2_520 => new N64ControllerState(N64Button.A, 0, 0),
                        >= 2_800 and < 2_820 => new N64ControllerState(N64Button.A, 0, 0),
                        _ => N64ControllerState.Neutral
                    };
                if (field >= 3_000 &&
                    machine.Memory.ReadUInt16(0x80331484) != ushort.MaxValue)
                {
                    controllerState = field % 90 < 20
                        ? new N64ControllerState(N64Button.A, 0, 0)
                        : N64ControllerState.Neutral;
                }
                else if (field >= 3_000 &&
                    machine.Memory.ReadUInt16(0x8033BACA) == 1 &&
                    machine.Memory.ReadUInt32(0x8033B17C) is not (0 or 0x04001301))
                {
                    if (playableField < 0)
                    {
                        playableField = field;
                        marioPositionBeforeMovement = ReadSm64MarioPosition(machine.Memory);
                    }

                    controllerState = new N64ControllerState(N64Button.None, 0, 60);
                }

                machine.SetControllerState(
                    1,
                    controllerState);
            }

            machine.RunFrame();
            fieldsRun = field + 1;
            if (playableField >= 0 && ++playableFrames >= 120)
            {
                break;
            }
            var buttonDown = machine.Memory.ReadUInt16(0x8033AFA0);
            var buttonPressed = machine.Memory.ReadUInt16(0x8033AFA2);
            var rawPad = machine.Memory.ReadUInt16(0x8033AFF8);
            observedButtonDown |= buttonDown;
            observedButtonPressed |= buttonPressed;
            observedRawPad |= rawPad;
            if (autoplay &&
                (field is >= 295 and < 330 ||
                 field is >= 475 and < 510 ||
                 field is >= 645 and < 680 ||
                 buttonPressed != 0))
            {
                controllerTransitions.Add(
                    $"field={field} p1=0x{machine.Memory.ReadUInt32(0x8032D5E4):X8} " +
                    $"down=0x{buttonDown:X4} pressed=0x{buttonPressed:X4} " +
                    $"pad=0x{rawPad:X4} pif=0x{machine.Memory.LastControllerStateWord:X8}");
            }
            if (machine.GraphicsTasksSubmitted == previousGraphicsTasks)
            {
                continue;
            }

            previousGraphicsTasks = machine.GraphicsTasksSubmitted;
            var candidate = machine.CurrentFrame.ToArray();
            var nonBlackPixels = candidate.Count(pixel => (pixel & 0x00FFFFFF) != 0);
            if (nonBlackPixels > mostNonBlackPixels)
            {
                bestFrame = candidate;
                mostNonBlackPixels = nonBlackPixels;
                bestFrameField = field;
            }
        }
        machine.RunInstructions(100_000);

        foreach (var (name, address) in new (string Name, uint Address)[]
                 {
                     ("gVblankHandler1", 0x8032D560),
                     ("gVblankHandler2", 0x8032D564),
                     ("gActiveSPTask", 0x8032D568),
                     ("sCurrentAudioSPTask", 0x8032D56C),
                     ("sCurrentDisplaySPTask", 0x8032D570),
                     ("sNextAudioSPTask", 0x8032D574),
                     ("sNextDisplaySPTask", 0x8032D578),
                     ("sAudioEnabled", 0x8032D57C),
                     ("gNumVblanks", 0x8032D580),
                     ("gGlobalTimer", 0x8032D5D4),
                     ("gGfxSPTask", 0x8033B068),
                     ("gDisplayListHead", 0x8033B06C),
                     ("gGfxPool", 0x8033B074),
                     ("gControllerBits", 0x8033B078)
                 })
        {
            output.WriteLine($"{name}=0x{machine.Memory.ReadUInt32(address):X8}");
        }

        foreach (var (name, address) in new (string Name, uint Address)[]
                 {
                     ("gIntrMesgQueue", 0x8033AE08),
                     ("gSPTaskMesgQueue", 0x8033AE20),
                     ("gDmaMesgQueue", 0x8033AF60),
                     ("gSIEventMesgQueue", 0x8033AF78),
                     ("gGameVblankQueue", 0x8033B010),
                     ("gGfxVblankQueue", 0x8033B028)
                 })
        {
            output.WriteLine(
                $"{name}: mt=0x{machine.Memory.ReadUInt32(address):X8} " +
                $"full=0x{machine.Memory.ReadUInt32(address + 4):X8} " +
                $"valid={machine.Memory.ReadUInt32(address + 8)} " +
                $"first={machine.Memory.ReadUInt32(address + 12)} " +
                $"count={machine.Memory.ReadUInt32(address + 16)} " +
                $"msg=0x{machine.Memory.ReadUInt32(address + 20):X8}");
        }

        foreach (var (name, thread) in new (string Name, uint Address)[]
                 {
                     ("main", 0x8033A8E0),
                     ("game", 0x8033AA90),
                     ("sound", 0x8033AC40)
                 })
        {
            output.WriteLine(
                $"{name}: next=0x{machine.Memory.ReadUInt32(thread):X8} " +
                $"pri={machine.Memory.ReadUInt32(thread + 4)} " +
                $"queue=0x{machine.Memory.ReadUInt32(thread + 8):X8} " +
                $"tlnext=0x{machine.Memory.ReadUInt32(thread + 12):X8} " +
                $"state=0x{machine.Memory.ReadUInt32(thread + 0x10):X8} " +
                $"sp=0x{machine.Memory.ReadUInt64(thread + 0xF0):X16} " +
                $"ra=0x{machine.Memory.ReadUInt64(thread + 0x100):X16} " +
                $"status=0x{machine.Memory.ReadUInt32(thread + 0x118):X8} " +
                $"epc=0x{machine.Memory.ReadUInt32(thread + 0x11C):X8}");
        }

        foreach (var thread in new[] { 0x8033A8E0u, 0x8033AA90u, 0x8033AC40u })
        {
            var references = new List<uint>();
            for (var address = 0x80000000u; address < 0x80800000u; address += 4)
            {
                if (machine.Memory.ReadUInt32(address) == thread)
                {
                    references.Add(address);
                }
            }

            output.WriteLine(
                $"references to 0x{thread:X8}: " +
                string.Join(", ", references.Select(address => $"0x{address:X8}")));
        }

        foreach (var address in new[] { 0x80365D60u, 0x80365D88u, 0x803670B0u })
        {
            output.WriteLine(
                $"dynamic 0x{address:X8}: " +
                string.Join(
                    " ",
                    Enumerable.Range(0, 8)
                        .Select(index => $"0x{machine.Memory.ReadUInt32(address + ((uint)index * 4)):X8}")));
        }

        output.WriteLine(
            $"tasks gfx={machine.GraphicsTasksSubmitted} audio={machine.AudioTasksSubmitted} " +
            $"ai={machine.Memory.AudioDmasCompleted} last={machine.LastRspTask}");
        output.WriteLine(
            $"VI origin=0x{machine.Memory.ViOrigin:X8} width={machine.Memory.ViWidth} " +
            $"colors={machine.CurrentFrame.ToArray().Distinct().Count()} " +
            $"RDP commands={machine.Renderer.CommandsProcessed} " +
            $"lists={machine.Renderer.DisplayListsProcessed} " +
            $"fills={machine.Renderer.FillRectanglesDrawn} " +
            $"vertices={machine.Renderer.VerticesTransformed} " +
            $"triangles={machine.Renderer.TrianglesDrawn} " +
            $"texture-rects={machine.Renderer.TextureRectanglesDrawn} " +
            $"textured-pixels={machine.Renderer.TexturedPixelsDrawn} " +
            $"depth-rejected={machine.Renderer.DepthPixelsRejected} " +
            $"clip-rejected={machine.Renderer.TriviallyClippedTriangles} " +
            $"max-triangle={machine.Renderer.MaximumTriangleWidth}x" +
            $"{machine.Renderer.MaximumTriangleHeight} " +
            $"unsupported={machine.Renderer.UnsupportedCommands} " +
            $"opcodes={string.Join(", ", machine.Renderer.UnsupportedCommandCounts.OrderBy(pair => pair.Key).Select(pair => $"0x{pair.Key:X2}:{pair.Value}"))}");
        output.WriteLine(
            $"PC=0x{machine.Cpu.ProgramCounter:X8} " +
            $"running=0x{machine.Memory.ReadUInt32(0x803359B0):X8} " +
            $"runq=0x{machine.Memory.ReadUInt32(0x803359A8):X8} " +
            $"CP0 count=0x{machine.Cpu.ReadCoprocessor0(9):X8} " +
            $"compare=0x{machine.Cpu.ReadCoprocessor0(11):X8} " +
            $"status=0x{machine.Cpu.ReadCoprocessor0(12):X8} " +
            $"cause=0x{machine.Cpu.ReadCoprocessor0(13):X8}");
        output.WriteLine(
            $"SM64 state save={machine.Memory.ReadUInt16(0x8032DDF4)} " +
            $"level={machine.Memory.ReadUInt16(0x8032DDF8)} " +
            $"course={machine.Memory.ReadUInt16(0x8033BAC6)} " +
            $"area={machine.Memory.ReadUInt16(0x8033BACA)} " +
            $"dialog={machine.Memory.ReadUInt16(0x80331484)} " +
            $"action=0x{machine.Memory.ReadUInt32(0x8033B17C):X8} " +
            $"mario={marioPositionBeforeMovement}->{ReadSm64MarioPosition(machine.Memory)} " +
            $"playable-field={playableField} fields={fieldsRun} " +
            $"controller-polls={machine.Memory.ControllerPolls} " +
            $"active-polls={machine.Memory.NonNeutralControllerPolls} " +
            $"last-controller=0x{machine.Memory.LastControllerStateWord:X8} " +
            $"si={machine.Memory.SiDmasCompleted}/{machine.Memory.SiDmasStarted} " +
            $"si-status=0x{machine.Memory.ReadUInt32(0xA4800018):X8} " +
            $"pif-control=0x{machine.Memory.PifRam[63]:X2} " +
            $"status-queries={machine.Memory.ControllerStatusQueries} " +
            $"read-commands={machine.Memory.ControllerReadCommands} " +
            $"game-buttons=0x{observedButtonDown:X4}/0x{observedButtonPressed:X4} " +
            $"raw-pad=0x{observedRawPad:X4}");
        output.WriteLine(
            "last-pif-write=" +
            Convert.ToHexString(machine.Memory.LastPifWrite));
        output.WriteLine(
            "controller-transitions=" +
            Environment.NewLine +
            string.Join(Environment.NewLine, controllerTransitions));
        output.WriteLine(
            "gd_exit code: " +
            string.Join(
                " ",
                Enumerable.Range(0, 16)
                    .Select(index =>
                        $"0x{machine.Memory.ReadUInt32(0x8019BB0C + ((uint)index * 4)):X8}")));
        output.WriteLine(
            "fatal_printf code: " +
            string.Join(
                " ",
                Enumerable.Range(0, 32)
                    .Select(index =>
                        $"0x{machine.Memory.ReadUInt32(0x8018D298 + ((uint)index * 4)):X8}")));
        output.WriteLine(
            "proc_dynlist code: " +
            string.Join(
                " ",
                Enumerable.Range(0, 24)
                    .Select(index =>
                        $"0x{machine.Memory.ReadUInt32(0x80183B20 + ((uint)index * 4)):X8}")));
        output.WriteLine(
            "game stack: " +
            string.Join(
                " ",
                Enumerable.Range(0, 40)
                    .Select(index =>
                        $"0x{machine.Memory.ReadUInt32((uint)machine.Cpu.Registers[29] + ((uint)index * 4)):X8}")));
        foreach (var (name, address) in new[]
                 {
                     ("projection", 0x00220D40u),
                     ("model-view", 0x00220D00u),
                     ("viewport", 0x00220CC0u),
                     ("model-a", 0x0021FB80u),
                     ("model-b", 0x0021FB40u)
                 })
        {
            output.WriteLine(
                $"{name}= " +
                string.Join(
                    " | ",
                    Enumerable.Range(0, 4).Select(row =>
                        string.Join(
                            " ",
                            Enumerable.Range(0, 4).Select(column =>
                                ReadN64MatrixElement(
                                    machine.Memory,
                                    address,
                                    (row * 4) + column).ToString("0.000"))))));
        }
        foreach (var address in new[] { 0x00076A40u, 0x00076A78u, 0x00076AA8u })
        {
            output.WriteLine(
                $"intro-dl 0x{address:X8}= " +
                string.Join(
                    " ",
                    Enumerable.Range(0, 12)
                        .Select(index =>
                            $"0x{machine.Memory.ReadUInt32(address + ((uint)index * 4)):X8}")));
        }

        if (string.Equals(
                Environment.GetEnvironmentVariable("PIXEL64_DUMP_FRAME"),
                "1",
                StringComparison.Ordinal))
        {
            var framePath = Path.Combine(Path.GetTempPath(), "pixel64-sm64-frame.ppm");
            using var frame = File.Create(framePath);
            using var writer = new BinaryWriter(frame);
            writer.Write(System.Text.Encoding.ASCII.GetBytes(
                $"P6\n{machine.Width} {machine.Height}\n255\n"));
            foreach (var pixel in machine.CurrentFrame)
            {
                writer.Write((byte)(pixel >> 16));
                writer.Write((byte)(pixel >> 8));
                writer.Write((byte)pixel);
            }

            output.WriteLine($"frame={framePath}");

            var renderTargetPath = Path.Combine(Path.GetTempPath(), "pixel64-sm64-render-target.ppm");
            using var renderTarget = File.Create(renderTargetPath);
            using var renderTargetWriter = new BinaryWriter(renderTarget);
            renderTargetWriter.Write(System.Text.Encoding.ASCII.GetBytes(
                $"P6\n{machine.Width} {machine.Height}\n255\n"));
            for (var y = 0; y < machine.Height; y++)
            {
                for (var x = 0; x < machine.Width; x++)
                {
                    var source =
                        machine.Renderer.ColorImageAddress +
                        (uint)(((y * machine.Renderer.ColorImageWidth) + x) * 2);
                    var pixel = machine.Memory.ReadUInt16(source);
                    var red = (byte)((((pixel >> 11) & 31) << 3) | ((pixel >> 13) & 7));
                    var green = (byte)((((pixel >> 6) & 31) << 3) | ((pixel >> 8) & 7));
                    var blue = (byte)((((pixel >> 1) & 31) << 3) | ((pixel >> 3) & 7));
                    renderTargetWriter.Write(red);
                    renderTargetWriter.Write(green);
                    renderTargetWriter.Write(blue);
                }
            }

            output.WriteLine(
                $"render-target={renderTargetPath} " +
                $"address=0x{machine.Renderer.ColorImageAddress:X8}");
            if (bestFrame is not null)
            {
                var bestFramePath = Path.Combine(Path.GetTempPath(), "pixel64-sm64-best-frame.ppm");
                using var bestFrameStream = File.Create(bestFramePath);
                using var bestFrameWriter = new BinaryWriter(bestFrameStream);
                bestFrameWriter.Write(System.Text.Encoding.ASCII.GetBytes(
                    $"P6\n{machine.Width} {machine.Height}\n255\n"));
                foreach (var pixel in bestFrame)
                {
                    bestFrameWriter.Write((byte)(pixel >> 16));
                    bestFrameWriter.Write((byte)(pixel >> 8));
                    bestFrameWriter.Write((byte)pixel);
                }

                output.WriteLine(
                    $"best-frame={bestFramePath} field={bestFrameField} " +
                    $"non-black={mostNonBlackPixels}");
            }
        }

        if (machine.LastGraphicsTask is { Type: 1 } task)
        {
            for (var offset = 0u; offset < task.DataSize; offset += 8)
            {
                output.WriteLine(
                    $"DL 0x{task.DataPointer + offset:X8}: " +
                    $"0x{machine.Memory.ReadUInt32(task.DataPointer + offset):X8} " +
                    $"0x{machine.Memory.ReadUInt32(task.DataPointer + offset + 4):X8}");
            }
        }
    }

    private static Vector3 ReadSm64MarioPosition(N64Memory memory) =>
        new(
            BitConverter.Int32BitsToSingle(unchecked((int)memory.ReadUInt32(0x8033B1AC))),
            BitConverter.Int32BitsToSingle(unchecked((int)memory.ReadUInt32(0x8033B1B0))),
            BitConverter.Int32BitsToSingle(unchecked((int)memory.ReadUInt32(0x8033B1B4))));

    private static float ReadN64MatrixElement(N64Memory memory, uint address, int index)
    {
        var integer = memory.ReadUInt16(address + (uint)(index * 2));
        var fraction = memory.ReadUInt16(address + 32 + (uint)(index * 2));
        var fixedPoint = ((uint)integer << 16) | fraction;
        return unchecked((int)fixedPoint) / 65536f;
    }
}
