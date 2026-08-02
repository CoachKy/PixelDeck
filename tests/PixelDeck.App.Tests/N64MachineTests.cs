using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
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

    [Theory]
    [InlineData(N64Cic.Cic6102, 0x80125C00u)]
    [InlineData(N64Cic.Cic6103, 0x80025C00u)]
    [InlineData(N64Cic.Cic6106, 0x7FF25C00u)]
    public void CartridgeEntryPointReflectsCicIpl3Relocation(
        N64Cic cic,
        uint expected)
    {
        Assert.Equal(
            expected,
            N64Cartridge.AdjustEntryPointForCic(0x80125C00, cic));
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
    public void MachineExposesItsGraphicsBackendThroughThePluginBoundary()
    {
        var machine = N64Machine.Create(
            N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));

        Assert.Same(machine.Renderer, machine.GraphicsBackend);
        Assert.Equal("Pixel64 Fast3D software renderer", machine.GraphicsBackend.Name);
    }

    [Fact]
    public void GraphicsCaptureRoundTripsCompressedRdramAndTaskExactly()
    {
        var rdram = new byte[N64Memory.RdramSize];
        rdram[0x1234] = 0x56;
        rdram[^1] = 0x78;
        var task = new N64RspTask(
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 0x1234, 14, 15, 16);
        var capture = N64GraphicsTaskCapture.Create(task, rdram);
        using var stream = new MemoryStream();

        capture.Write(stream);
        Assert.True(stream.Length < 128 * 1024);
        stream.Position = 0;
        var restored = N64GraphicsTaskCapture.Read(stream);

        Assert.Equal(task, restored.Task);
        Assert.Equal(capture.RdramSha256, restored.RdramSha256);
        Assert.True(capture.Rdram.Span.SequenceEqual(restored.Rdram.Span));
    }

    [Fact]
    public void GraphicsCaptureRejectsAValidPayloadWithATamperedChecksum()
    {
        var task = new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 8, 0, 0);
        var capture = N64GraphicsTaskCapture.Create(task, new byte[N64Memory.RdramSize]);
        using var stream = new MemoryStream();
        capture.Write(stream);
        var bytes = stream.ToArray();
        const int checksumOffset = 8 + 4 + (16 * 4) + 4 + 4;
        bytes[checksumOffset] ^= 0x80;

        Assert.Throws<InvalidDataException>(
            () => N64GraphicsTaskCapture.Read(new MemoryStream(bytes, writable: false)));
    }

    [Fact]
    public void GraphicsCaptureReplaysTheSameTaskDeterministically()
    {
        var memory = new N64Memory(
            N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt32(0x400, 0x0000FFFF);
        memory.WriteUInt32(0x200, 0xFF180003);
        memory.WriteUInt32(0x204, 0x00000400);
        memory.WriteUInt32(0x208, 0xEF000000);
        memory.WriteUInt32(0x20C, 0x00404000);
        memory.WriteUInt32(0x210, 0xFC000000);
        memory.WriteUInt32(0x214, 0x00018600);
        memory.WriteUInt32(0x218, 0xFA000000);
        memory.WriteUInt32(0x21C, 0xFF000080);
        memory.WriteUInt32(0x220, 0xF6000000);
        memory.WriteUInt32(0x224, 0);
        memory.WriteUInt32(0x228, 0xB8000000);
        memory.WriteUInt32(0x22C, 0);
        var task = new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 48, 0, 0);
        var capture = N64GraphicsTaskCapture.Create(task, memory.Rdram);

        var first = N64GraphicsReplay.Replay(capture);
        var second = N64GraphicsReplay.Replay(capture);

        Assert.Equal(first.RdramSha256, second.RdramSha256);
        Assert.Equal(
            0x80007FFFu,
            BinaryPrimitives.ReadUInt32BigEndian(first.Rdram.Span.Slice(0x400, 4)));
        Assert.Equal(1, first.RdpState?.FramebufferPixelsBlended);
        Assert.Equal("Pixel64 Fast3D software renderer", first.BackendName);
    }

    [Fact]
    public void RdpTraceRoundTripsAndReplaysDirectPacketsDeterministically()
    {
        var memory = new N64Memory(
            N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt32(0x400, 0x0000FFFF);
        memory.WriteUInt32(0x200, 0xFF180003);
        memory.WriteUInt32(0x204, 0x00000400);
        memory.WriteUInt32(0x208, 0xEF000000);
        memory.WriteUInt32(0x20C, 0x00404000);
        memory.WriteUInt32(0x210, 0xFC000000);
        memory.WriteUInt32(0x214, 0x00018600);
        memory.WriteUInt32(0x218, 0xFA000000);
        memory.WriteUInt32(0x21C, 0xFF000080);
        memory.WriteUInt32(0x220, 0xF6000000);
        memory.WriteUInt32(0x224, 0);
        memory.WriteUInt32(0x228, 0xB8000000);
        memory.WriteUInt32(0x22C, 0);
        var task = new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 48, 0, 0);
        var graphicsCapture = N64GraphicsTaskCapture.Create(task, memory.Rdram);

        var trace = N64RdpTrace.Capture(graphicsCapture);
        Assert.True(trace.IsComplete);
        Assert.Equal(5, trace.Commands.Count);
        Assert.Equal([0xFF, 0xEF, 0xFC, 0xFA, 0xF6], trace.Commands.Select(x => x.Opcode));
        Assert.Equal(0x00000400u, trace.Commands[0].Words.Span[1]);

        using var stream = new MemoryStream();
        trace.Write(stream);
        stream.Position = 0;
        var restored = N64RdpTrace.Read(stream);
        var first = N64RdpReplay.Replay(restored);
        var second = N64RdpReplay.Replay(restored);

        Assert.Equal(trace.TraceSha256, restored.TraceSha256);
        Assert.Equal(first.RdramSha256, second.RdramSha256);
        Assert.Equal(
            0x80007FFFu,
            BinaryPrimitives.ReadUInt32BigEndian(first.Rdram.Span.Slice(0x400, 4)));
        Assert.Equal(1, first.RdpState.FramebufferPixelsBlended);
        Assert.True(first.SourceTraceComplete);
    }

    [Fact]
    public void RdpTraceRejectsTamperingAndTracksUnsupportedSourceCommands()
    {
        var memory = new N64Memory(
            N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt32(0x200, 0xBF000000);
        memory.WriteUInt32(0x204, 0x00000000);
        memory.WriteUInt32(0x208, 0xAA000000);
        memory.WriteUInt32(0x20C, 0);
        memory.WriteUInt32(0x210, 0xB8000000);
        memory.WriteUInt32(0x214, 0);
        var task = new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 24, 0, 0);
        var trace = N64RdpTrace.Capture(
            N64GraphicsTaskCapture.Create(task, memory.Rdram));
        Assert.False(trace.IsComplete);
        Assert.Equal(0, trace.OmittedHlePrimitiveCommands);
        Assert.Equal(1, trace.UnsupportedSourceCommands);

        using var stream = new MemoryStream();
        trace.Write(stream);
        var bytes = stream.ToArray();
        const int checksumOffset = 8 + (7 * 4);
        bytes[checksumOffset] ^= 0x40;

        Assert.Throws<InvalidDataException>(
            () => N64RdpTrace.Read(new MemoryStream(bytes, writable: false)));

        using var validStream = new MemoryStream();
        trace.Write(validStream);
        var truncated = validStream.ToArray()[..^1];
        Assert.Throws<InvalidDataException>(
            () => N64RdpTrace.Read(new MemoryStream(truncated, writable: false)));
    }

    [Fact]
    public void MachineCapturesOnlyTheNextGraphicsTaskWhenRequested()
    {
        var machine = N64Machine.Create(
            N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0x80001000, 0xB8000000);
        machine.Memory.WriteUInt32(0x80001004, 0);
        machine.Memory.WriteUInt32(0xA4000FC0, 1);
        machine.Memory.WriteUInt32(0xA4000FF0, 0x00001000);
        machine.Memory.WriteUInt32(0xA4000FF4, 8);
        machine.RequestGraphicsTaskCapture();
        machine.Memory.WriteUInt32(0xA4040010, 1);

        machine.RunInstructions(1);

        Assert.NotNull(machine.LastGraphicsCapture);
        Assert.Equal(0x1000u, machine.LastGraphicsCapture.Task.DataPointer);
        Assert.Equal(0xB8000000u, BinaryPrimitives.ReadUInt32BigEndian(
            machine.LastGraphicsCapture.Rdram.Span.Slice(0x1000, 4)));
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
    // The horizontal scan window is a constant 640 clocks wide, so X_SCALE
    // alone selects the visible width: SM64/Quest 64/Ocarina display 320,
    // GoldenEye 007 displays 440. The last case is the one that matters —
    // a cartridge striding its frame buffer wider than it displays must be
    // sized by the scan window, not by VI_WIDTH.
    [InlineData(320u, 0x200u, 0x400u, 320, 237)]
    [InlineData(440u, 0x2C0u, 0x580u, 440, 325)]
    [InlineData(640u, 0x400u, 0x400u, 640, 237)]
    [InlineData(640u, 0x200u, 0x400u, 320, 237)]
    public void VideoResolutionFollowsTheVideoInterfaceRegisters(
        uint viWidth,
        uint horizontalScale,
        uint verticalScale,
        int expectedWidth,
        int expectedHeight)
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0xA4400008, viWidth);
        machine.Memory.WriteUInt32(0xA4400024, (108u << 16) | 748u);
        machine.Memory.WriteUInt32(0xA4400028, (37u << 16) | 511u);
        machine.Memory.WriteUInt32(0xA4400030, horizontalScale);
        machine.Memory.WriteUInt32(0xA4400034, verticalScale);

        Assert.Equal(expectedWidth, machine.Width);
        Assert.Equal(expectedHeight, machine.Height);
        Assert.Equal(expectedWidth * expectedHeight, machine.CurrentFrame.Length);
    }

    [Fact]
    public void APakTransferThePortCannotServiceStillAnswers()
    {
        // Super Mario 64 blocks on the PIF until every channel reports back.
        // A pak command with lengths the port cannot service must set the
        // "no device" bit rather than returning nothing, or the game spins
        // forever on the next controller poll.
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));

        // Channel 0 issues a pak read (command 0x02, three transmit bytes) but
        // asks for only four receive bytes instead of the 33 a pak read needs.
        memory.Rdram[0x100] = 3;
        memory.Rdram[0x101] = 4;
        memory.Rdram[0x102] = 0x02;
        memory.Rdram[0x13F] = 1;

        memory.WriteUInt32(0xA4800000, 0x100);
        memory.WriteUInt32(0xA4800010, 0x1FC007C0);
        memory.AdvanceCpuTicks(256);
        memory.WriteUInt32(0xA4800004, 0x1FC007C0);
        memory.AdvanceCpuTicks(256);

        Assert.Equal(0x80, memory.Rdram[0x101] & 0x80);
    }

    [Fact]
    public void AnUnreadableMicrocodeBannerHoldsTheLastIdentifiedMicrocode()
    {
        // WrestleMania 2000 submits a task with an unreadable banner about 25
        // seconds in. Classifying that as Fast3D decodes the rest of its
        // F3DEX2 display lists against the wrong opcode table and rendering
        // stops, so an unknown banner must not downgrade the detection.
        Assert.Equal(
            Fast3dRenderer.N64Microcode.F3dex2,
            Fast3dRenderer.ClassifyMicrocode(
                banner: null,
                crc32: 0x847DBABB,
                Fast3dRenderer.N64Microcode.F3dex2));

        // With nothing identified yet the legacy default still applies.
        Assert.Equal(
            Fast3dRenderer.N64Microcode.Fast3d,
            Fast3dRenderer.ClassifyMicrocode(banner: null, crc32: 0));

        // A readable banner always wins over what came before.
        Assert.Equal(
            Fast3dRenderer.N64Microcode.F3dex2,
            Fast3dRenderer.ClassifyMicrocode(
                "RSP Gfx ucode F3DEX.NoN 2.08",
                0,
                Fast3dRenderer.N64Microcode.Fast3d));
        Assert.Equal(
            Fast3dRenderer.N64Microcode.Fast3d,
            Fast3dRenderer.ClassifyMicrocode(
                "RSP SW Version: Cool",
                0,
                Fast3dRenderer.N64Microcode.F3dex2));
    }

    [Fact]
    public void VideoResolutionKeepsTheLastValidSizeWhileTheInterfaceIsUnprogrammed()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));

        machine.Memory.WriteUInt32(0xA4400008, 0);

        Assert.Equal(320, machine.Width);
        Assert.Equal(240, machine.Height);
    }

    [Fact]
    public void VideoResolutionIgnoresTheScanWindowUntilAFrameBufferIsAllocated()
    {
        // A cartridge programs the video interface a register at a time, so the
        // scan window is readable while VI_WIDTH is still zero. Sizing from it
        // during that window presents whatever RDRAM sits under the origin.
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));

        machine.Memory.WriteUInt32(0xA4400008, 0);
        machine.Memory.WriteUInt32(0xA4400024, (108u << 16) | 748u);
        machine.Memory.WriteUInt32(0xA4400028, (37u << 16) | 511u);
        machine.Memory.WriteUInt32(0xA4400030, 0x400u);
        machine.Memory.WriteUInt32(0xA4400034, 0x400u);

        Assert.Equal(320, machine.Width);
        Assert.Equal(240, machine.Height);

        machine.Memory.WriteUInt32(0xA4400008, 640u);

        Assert.Equal(640, machine.Width);
        Assert.Equal(237, machine.Height);
    }

    [Fact]
    public void VideoInterfaceBlacksTheRetainedFrameWhileHorizontalOutputIsDisabled()
    {
        var machine = N64Machine.Create(
            N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        const uint framebuffer = 0x1000;
        machine.Memory.WriteUInt16(framebuffer, 0xF801);
        machine.Memory.WriteUInt32(0xA4400000, 2);
        machine.Memory.WriteUInt32(0xA4400004, framebuffer);
        machine.Memory.WriteUInt32(0xA4400008, 320);
        machine.Memory.WriteUInt32(0xA4400024, (108u << 16) | 748u);
        machine.Memory.WriteUInt32(0xA4400028, (37u << 16) | 511u);
        machine.Memory.WriteUInt32(0xA4400030, 0x200);
        machine.Memory.WriteUInt32(0xA4400034, 0x400);

        machine.RenderVideoInterface();
        Assert.Equal(0xFFFF0000u, machine.CurrentFrame[0]);

        // osViBlack keeps the configured framebuffer and dimensions but
        // suppresses scanning by replacing H_START with zero.
        machine.Memory.WriteUInt32(0xA4400024, 0);
        machine.RenderVideoInterface();

        Assert.Equal(320, machine.Width);
        Assert.Equal(237, machine.Height);
        Assert.All(machine.CurrentFrame.ToArray(), pixel => Assert.Equal(0xFF000000u, pixel));
    }

    [Theory]
    [InlineData("NDOE", N64SaveType.Eeprom16Kbit, 2 * 1024, ".eep")]
    [InlineData("CZLE", N64SaveType.Sram256Kbit, 32 * 1024, ".sra")]
    [InlineData("NGEE", N64SaveType.Eeprom4Kbit, 512, ".eep")]
    [InlineData("NKGE", N64SaveType.Sram256Kbit, 32 * 1024, ".sra")]
    [InlineData("NMFE", N64SaveType.Sram256Kbit, 32 * 1024, ".sra")]
    [InlineData("NM8E", N64SaveType.Eeprom16Kbit, 2 * 1024, ".eep")]
    [InlineData("NPXE", N64SaveType.Eeprom4Kbit, 512, ".eep")]
    [InlineData("NGXE", N64SaveType.None, 32 * 1024, ".mpk")]
    [InlineData("NG5E", N64SaveType.None, 32 * 1024, ".mpk")]
    [InlineData("NETE", N64SaveType.None, 32 * 1024, ".mpk")]
    [InlineData("NWXE", N64SaveType.Sram256Kbit, 32 * 1024, ".sra")]
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

    [Theory]
    [InlineData("NGXE", true, true, false)]
    [InlineData("NKGE", true, false, false)]
    [InlineData("NMFE", false, false, true)]
    [InlineData("NKTE", true, true, false)]
    [InlineData("NM8E", false, false, true)]
    [InlineData("NG5E", true, true, false)]
    [InlineData("NETE", true, true, false)]
    [InlineData("NWXE", true, false, false)]
    public void CartridgeProfileDeclaresItsControllerAccessories(
        string gameCode,
        bool supportsControllerPak,
        bool usesControllerPak,
        bool usesTransferPak)
    {
        var image = N64TestSupport.CreateCartridgeImage();
        SetGameCode(image, gameCode);

        var cartridge = N64Cartridge.FromBytes(image);

        Assert.Equal(supportsControllerPak, cartridge.SupportsControllerPak);
        Assert.Equal(usesControllerPak, cartridge.UsesControllerPak);
        Assert.Equal(usesTransferPak, cartridge.UsesTransferPak);
    }

    [Fact]
    public void Quest64SeesAFormattedControllerPakInsteadOfARumblePak()
    {
        var image = N64TestSupport.CreateCartridgeImage();
        SetGameCode(image, "NETE");
        var cartridge = N64Cartridge.FromBytes(image);
        var memory = new N64Memory(cartridge);

        Assert.Equal(N64SaveType.None, cartridge.SaveType);
        Assert.True(cartridge.UsesControllerPak);
        Assert.Equal(N64Memory.ControllerPakSize, memory.ControllerPak.Length);

        // Read the primary ID block at 0x0020 through the PIF, exactly as the
        // libultra Controller Pak probe does.
        memory.Rdram[0x100] = 3;
        memory.Rdram[0x101] = 33;
        memory.Rdram[0x102] = 0x02;
        memory.Rdram[0x103] = 0x00;
        memory.Rdram[0x104] = 0x20;
        memory.Rdram[0x126] = 0xFE;
        RunPifRoundTrip(memory);

        Assert.Equal(0, memory.Rdram[0x101] & 0x80);
        Assert.Equal(0x0001, BinaryPrimitives.ReadUInt16BigEndian(memory.Rdram.AsSpan(0x11D, 2)));
        Assert.Equal(0x01, memory.Rdram[0x11F]);
        var sum = BinaryPrimitives.ReadUInt16BigEndian(memory.Rdram.AsSpan(0x121, 2));
        var inverseSum = BinaryPrimitives.ReadUInt16BigEndian(memory.Rdram.AsSpan(0x123, 2));
        Assert.Equal(0xFFF2, unchecked((ushort)(sum + inverseSum)));

        // The primary inode table contains 123 free pages and has a matching
        // backup. A zero-filled fake pak does not satisfy these invariants.
        Assert.Equal(0x71, memory.ControllerPak[0x101]);
        Assert.Equal(
            memory.ControllerPak.AsSpan(0x100, 0x100).ToArray(),
            memory.ControllerPak.AsSpan(0x200, 0x100).ToArray());
        for (var page = 5; page < 128; page++)
        {
            Assert.Equal(
                0x0003,
                BinaryPrimitives.ReadUInt16BigEndian(memory.ControllerPak.AsSpan(0x100 + (page * 2), 2)));
        }
    }

    [Fact]
    public void Quest64ControllerPakWritesPersistAcrossMachineInstances()
    {
        var root = Path.Combine(Path.GetTempPath(), $"Pixel64-mempak-{Guid.NewGuid():N}");
        var romPath = Path.Combine(root, "Quest64.z64");
        var savePath = Path.Combine(root, "Quest64.mpk");
        Directory.CreateDirectory(root);
        try
        {
            var image = N64TestSupport.CreateCartridgeImage();
            SetGameCode(image, "NETE");
            File.WriteAllBytes(romPath, image);
            var machine = N64Machine.Load(romPath, savePath);
            var expected = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

            // Write one data block at 0x0500 using Controller Pak command 3.
            machine.Memory.Rdram[0x100] = 35;
            machine.Memory.Rdram[0x101] = 1;
            machine.Memory.Rdram[0x102] = 0x03;
            machine.Memory.Rdram[0x103] = 0x05;
            machine.Memory.Rdram[0x104] = 0x00;
            expected.CopyTo(machine.Memory.Rdram, 0x105);
            machine.Memory.Rdram[0x126] = 0xFE;
            RunPifRoundTrip(machine.Memory);

            Assert.True(machine.Memory.ControllerPakDirty);
            Assert.Equal(expected, machine.Memory.ControllerPak.AsSpan(0x500, 32).ToArray());
            machine.FlushBatterySave();

            Assert.False(machine.Memory.ControllerPakDirty);
            Assert.Equal(N64Memory.ControllerPakSize, new FileInfo(savePath).Length);
            var restored = N64Machine.Load(romPath, savePath);
            Assert.Equal(expected, restored.Memory.ControllerPak.AsSpan(0x500, 32).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MarioKartPersistsCartridgeEepromAndControllerPakIndependently()
    {
        var root = Path.Combine(Path.GetTempPath(), $"Pixel64-dual-save-{Guid.NewGuid():N}");
        var romPath = Path.Combine(root, "MarioKart64.z64");
        var savePath = Path.Combine(root, "MarioKart64.eep");
        var controllerPakPath = Path.Combine(root, "MarioKart64.mpk");
        Directory.CreateDirectory(root);
        try
        {
            var image = N64TestSupport.CreateCartridgeImage();
            SetGameCode(image, "NKTE");
            File.WriteAllBytes(romPath, image);
            var machine = N64Machine.Load(romPath, savePath);
            var expectedEeprom = new byte[] { 0x4D, 0x41, 0x52, 0x49, 0x4F, 0x4B, 0x41, 0x52 };
            var expectedPak = Enumerable.Range(0x40, 32).Select(value => (byte)value).ToArray();

            // Write EEPROM block 3 through the cartridge save channel.
            machine.Memory.Rdram[0x104] = 10;
            machine.Memory.Rdram[0x105] = 1;
            machine.Memory.Rdram[0x106] = 0x05;
            machine.Memory.Rdram[0x107] = 0x03;
            expectedEeprom.CopyTo(machine.Memory.Rdram, 0x108);
            machine.Memory.Rdram[0x111] = 0xFE;
            RunPifRoundTrip(machine.Memory);

            // Write one ghost-data block through the Controller Pak channel.
            Array.Clear(machine.Memory.Rdram, 0x100, 0x40);
            machine.Memory.Rdram[0x100] = 35;
            machine.Memory.Rdram[0x101] = 1;
            machine.Memory.Rdram[0x102] = 0x03;
            machine.Memory.Rdram[0x103] = 0x05;
            machine.Memory.Rdram[0x104] = 0x00;
            expectedPak.CopyTo(machine.Memory.Rdram, 0x105);
            machine.Memory.Rdram[0x126] = 0xFE;
            RunPifRoundTrip(machine.Memory);

            Assert.True(machine.Memory.EepromDirty);
            Assert.True(machine.Memory.ControllerPakDirty);
            machine.FlushBatterySave();

            Assert.False(machine.Memory.EepromDirty);
            Assert.False(machine.Memory.ControllerPakDirty);
            Assert.Equal(512, new FileInfo(savePath).Length);
            Assert.Equal(N64Memory.ControllerPakSize, new FileInfo(controllerPakPath).Length);

            var restored = N64Machine.Load(romPath, savePath);
            Assert.Equal(expectedEeprom, restored.Memory.Eeprom.AsSpan(3 * 8, 8).ToArray());
            Assert.Equal(expectedPak, restored.Memory.ControllerPak.AsSpan(0x500, 32).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
    public void Cic6105RawRspBootTaskPerformsItsHandshakeDmas()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        // The x105 program submitted by IPL3 is identified by the sum of its
        // first 44 bytes. This synthetic signature exercises the dispatch
        // contract without embedding Nintendo's resident microcode.
        memory.SpImem.AsSpan(0, 44).Clear();
        memory.SpImem.AsSpan(0, 9).Fill(0xFF);
        memory.SpImem[9] = 0xEB;

        for (var index = 0; index < 0x1F0; index++)
        {
            memory.Rdram[0x1E8 + index] = (byte)(index ^ 0xA5);
        }

        BinaryPrimitives.WriteUInt32BigEndian(
            memory.Rdram.AsSpan(0x200, sizeof(uint)),
            0xAD170014);

        Assert.True(memory.TryExecuteCic6105BootTask());
        Assert.True(memory.Rdram.AsSpan(0x1E8, 0x1F0).SequenceEqual(
            memory.SpImem.AsSpan(0x120, 0x1F0)));
        Assert.Equal(0xAD170014u, memory.ReadUInt32(0xA02FE1C0));
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
        Assert.Equal(1, machine.Cpu.ExceptionsRaised);
        Assert.Equal(0, machine.Cpu.InterruptExceptionsRaised);
        Assert.Equal(1, machine.Cpu.NonInterruptExceptionsRaised);
        Assert.Equal(8, machine.Cpu.LastExceptionCode);
        Assert.Equal(0xA4000044u, machine.Cpu.LastExceptionAddress);
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

    [Theory]
    [InlineData(0x44800000u)] // MTC1 r0, f0
    [InlineData(0xC4000000u)] // LWC1 f0, 0(r0)
    [InlineData(0xD4000000u)] // LDC1 f0, 0(r0)
    [InlineData(0xE4000000u)] // SWC1 f0, 0(r0)
    [InlineData(0xF4000000u)] // SDC1 f0, 0(r0)
    public void Coprocessor1OperationsTrapWhenStatusCu1IsClear(uint instruction)
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0xA4000040, 0x40806000); // MTC0 r0, Status
        machine.Memory.WriteUInt32(0xA4000044, instruction);

        machine.RunInstructions(2);

        Assert.Equal(0x80000180u, machine.Cpu.ProgramCounter);
        Assert.Equal(0xA4000044u, machine.Cpu.ReadCoprocessor0(14));
        Assert.Equal(0x1000002Cu, machine.Cpu.ReadCoprocessor0(13) & 0x3000007Cu);
    }

    [Fact]
    public void DoublePrecisionUsesEvenOddRegisterPairsWhenStatusFrIsClear()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0xA4000040, 0x3C082000); // LUI t0, 0x2000 (CU1)
        machine.Memory.WriteUInt32(0xA4000044, 0x40886000); // MTC0 t0, Status (FR clear)
        machine.Memory.WriteUInt32(0xA4000048, 0x3C083FF8);
        machine.Memory.WriteUInt32(0xA400004C, 0x44800000);
        machine.Memory.WriteUInt32(0xA4000050, 0x44880800);
        machine.Memory.WriteUInt32(0xA4000054, 0x46200080);
        machine.Memory.WriteUInt32(0xA4000058, 0x44291000);

        machine.RunInstructions(7);

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
    public void IdleSelfBranchAdvancesClocksInBulkAndStopsForViInterrupt()
    {
        var machine = N64Machine.Create(
            N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0xA4000040, 0x34080401); // ORI t0, r0, IE | IP2 mask
        machine.Memory.WriteUInt32(0xA4000044, 0x40886000); // MTC0 t0, Status
        machine.Memory.WriteUInt32(0xA4000048, 0x1000FFFF); // BEQ r0, r0, self
        machine.Memory.WriteUInt32(0xA400004C, 0x00000000); // NOP delay slot
        machine.Memory.WriteUInt32(0xA4400018, 9);          // Ten lines per field
        machine.Memory.WriteUInt32(0xA440000C, 4);          // Interrupt on line four
        machine.Memory.WriteUInt32(0xA430000C, 0x80);       // Unmask VI
        var interruptTick = (machine.Memory.CpuTicksPerField * 4) / 10;

        machine.RunInstructions(interruptTick + 8);

        Assert.True(machine.Cpu.IdleInstructionsSkipped > 100_000);
        Assert.Equal(1, machine.Memory.VerticalInterruptsRaised);
        Assert.Equal(1, machine.Cpu.InterruptExceptionsRaised);
    }

    [Fact]
    public void SelfBranchWithDelaySlotSideEffectIsNeverAccelerated()
    {
        var machine = N64Machine.Create(
            N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0xA4000040, 0x1000FFFF); // BEQ r0, r0, self
        machine.Memory.WriteUInt32(0xA4000044, 0x25080001); // ADDIU t0, t0, 1
        var initialT0 = machine.Cpu.Registers[8];

        machine.RunInstructions(100);

        Assert.Equal(0, machine.Cpu.IdleInstructionsSkipped);
        Assert.Equal(initialT0 + 50, machine.Cpu.Registers[8]);
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
    public void AudioInterfaceContinuouslyConvertsTheCartridgeDacRateToHostRate()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        // An approximately 16 kHz cartridge rate must yield roughly two host
        // frames for each source frame. Split the ramp across two DMAs to also
        // verify that interpolation phase survives the boundary.
        memory.WriteUInt32(0xA4500010, 3042);
        memory.WriteUInt32(0xA4500008, 1);
        for (var frame = 0; frame < 8; frame++)
        {
            var sample = (ushort)(frame * 1_000);
            memory.WriteUInt16((uint)(0x1000 + (frame * 4)), sample);
            memory.WriteUInt16((uint)(0x1002 + (frame * 4)), sample);
        }

        memory.WriteUInt32(0xA4500000, 0x1000);
        memory.WriteUInt32(0xA4500004, 16);
        memory.WriteUInt32(0xA4500000, 0x1010);
        memory.WriteUInt32(0xA4500004, 16);

        var output = new float[64];
        var count = memory.ReadAudioSamples(output);

        Assert.InRange(memory.CurrentAudioSampleRate, 15_900, 16_100);
        Assert.InRange(count, 28, 32);
        Assert.Equal(0, count & 1);
        for (var index = 2; index < count; index += 2)
        {
            Assert.True(output[index] >= output[index - 2]);
            Assert.Equal(output[index], output[index + 1]);
        }
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
    public void SiPifSupportsTheFull16KbitEepromAddressSpace()
    {
        var image = N64TestSupport.CreateCartridgeImage();
        SetGameCode(image, "NDOE");
        var memory = new N64Memory(N64Cartridge.FromBytes(image));
        var expected = new byte[] { 0x47, 0x4F, 0x4C, 0x44, 0x45, 0x4E, 0x45, 0x59 };

        Assert.Equal(2 * 1024, memory.Eeprom.Length);

        // Channels 0-3 are controllers. Four empty descriptors advance to
        // channel 4, where command 5 writes one eight-byte EEPROM block.
        memory.Rdram[0x104] = 10;
        memory.Rdram[0x105] = 1;
        memory.Rdram[0x106] = 0x05;
        memory.Rdram[0x107] = 0xC1;
        expected.CopyTo(memory.Rdram, 0x108);
        memory.Rdram[0x111] = 0xFE;
        RunPifRoundTrip(memory);

        Assert.True(memory.EepromDirty);
        Assert.Equal(expected, memory.Eeprom.AsSpan(0xC1 * 8, 8).ToArray());

        // Reading the same high block proves that the address no longer
        // wraps through the 4-Kbit device's six-bit address mask.
        memory.Rdram.AsSpan(0x100, 64).Clear();
        memory.Rdram[0x104] = 2;
        memory.Rdram[0x105] = 8;
        memory.Rdram[0x106] = 0x04;
        memory.Rdram[0x107] = 0xC1;
        memory.Rdram[0x110] = 0xFE;
        RunPifRoundTrip(memory);

        Assert.Equal(expected, memory.Rdram.AsSpan(0x108, 8).ToArray());
    }

    [Theory]
    [InlineData("NPXE", 0x80)]
    [InlineData("NGEE", 0x80)]
    [InlineData("NDOE", 0xC0)]
    public void SiPifReportsTheInstalledEepromCapacity(string gameCode, byte expectedType)
    {
        var image = N64TestSupport.CreateCartridgeImage();
        SetGameCode(image, gameCode);
        var memory = new N64Memory(N64Cartridge.FromBytes(image));

        memory.Rdram[0x104] = 1;
        memory.Rdram[0x105] = 3;
        memory.Rdram[0x106] = 0x00;
        memory.Rdram[0x10A] = 0xFE;
        RunPifRoundTrip(memory);

        Assert.Equal(0x00, memory.Rdram[0x107]);
        Assert.Equal(expectedType, memory.Rdram[0x108]);
        Assert.Equal(0x00, memory.Rdram[0x109]);
    }

    [Fact]
    public void MachinePersistsAndReloadsEvery16KbitEepromBlock()
    {
        var root = Path.Combine(Path.GetTempPath(), $"Pixel64-eeprom-{Guid.NewGuid():N}");
        var romPath = Path.Combine(root, "DonkeyKong64.z64");
        var savePath = Path.Combine(root, "DonkeyKong64.eep");
        Directory.CreateDirectory(root);
        try
        {
            var image = N64TestSupport.CreateCartridgeImage();
            SetGameCode(image, "NDOE");
            File.WriteAllBytes(romPath, image);
            var machine = N64Machine.Load(romPath, savePath);
            var expected = new byte[] { 0x50, 0x49, 0x58, 0x45, 0x4C, 0x36, 0x34, 0x21 };

            machine.Memory.Rdram[0x104] = 10;
            machine.Memory.Rdram[0x105] = 1;
            machine.Memory.Rdram[0x106] = 0x05;
            machine.Memory.Rdram[0x107] = 0xFF;
            expected.CopyTo(machine.Memory.Rdram, 0x108);
            machine.Memory.Rdram[0x111] = 0xFE;
            RunPifRoundTrip(machine.Memory);
            machine.FlushBatterySave();

            Assert.Equal(2 * 1024, new FileInfo(savePath).Length);
            var restored = N64Machine.Load(romPath, savePath);
            Assert.Equal(expected, restored.Memory.Eeprom.AsSpan(0xFF * 8, 8).ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    private static void RunPifRoundTrip(N64Memory memory)
    {
        memory.Rdram[0x13F] = 1;
        memory.WriteUInt32(0xA4800000, 0x100);
        memory.WriteUInt32(0xA4800010, 0x1FC007C0);
        memory.AdvanceCpuTicks(256);
        memory.WriteUInt32(0xA4800004, 0x1FC007C0);
        memory.AdvanceCpuTicks(256);
    }

    private static void SetGameCode(byte[] image, string gameCode)
    {
        image[0x3B] = (byte)gameCode[0];
        image[0x3C] = (byte)gameCode[1];
        image[0x3D] = (byte)gameCode[2];
        image[0x3E] = (byte)gameCode[3];
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
    public void SaveStateStillLoadsVersionEightStatesThatPredateControllerPaks()
    {
        var machine = N64Machine.Create(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        machine.Memory.WriteUInt32(0x80000100, 0x12345678);
        var current = machine.SaveState();

        const int versionOffset = 8;
        const int payloadLengthOffset = 8 + 4 + 32;
        const int integrityOffset = payloadLengthOffset + 4;
        const int payloadOffset = integrityOffset + 32;
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(payloadLengthOffset, 4));
        var payload = current.AsSpan(payloadOffset, payloadLength);
        var pakOffset = payload.IndexOf(machine.Memory.ControllerPak);
        Assert.True(pakOffset >= 0);

        // Version 9 inserted the 32 KiB pak plus its dirty flag immediately
        // before the controller states. Removing that range recreates the
        // exact version 8 payload layout.
        var legacyPayload = new byte[payload.Length - N64Memory.ControllerPakSize - 1];
        payload[..pakOffset].CopyTo(legacyPayload);
        payload[(pakOffset + N64Memory.ControllerPakSize + 1)..]
            .CopyTo(legacyPayload.AsSpan(pakOffset));
        var legacy = new byte[payloadOffset + legacyPayload.Length];
        current.AsSpan(0, payloadOffset).CopyTo(legacy);
        BinaryPrimitives.WriteInt32LittleEndian(legacy.AsSpan(versionOffset, 4), 8);
        BinaryPrimitives.WriteInt32LittleEndian(
            legacy.AsSpan(payloadLengthOffset, 4),
            legacyPayload.Length);
        SHA256.HashData(legacyPayload).CopyTo(legacy, integrityOffset);
        legacyPayload.CopyTo(legacy, payloadOffset);

        machine.Memory.WriteUInt32(0x80000100, 0);
        machine.LoadState(legacy);

        Assert.Equal(0x12345678u, machine.Memory.ReadUInt32(0x80000100));
        Assert.Equal(N64Memory.ControllerPakSize, machine.Memory.ControllerPak.Length);
        Assert.False(machine.Memory.ControllerPakDirty);
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
    public void Fast3dLineRasterizesTheLegacyLineCommand()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt16(0x100, 0);
        memory.WriteUInt16(0x102, 0);
        memory.WriteUInt16(0x104, 0);
        memory.WriteByte(0x10C, 0xFF);
        memory.WriteByte(0x10D, 0);
        memory.WriteByte(0x10E, 0);
        memory.WriteByte(0x10F, 0xFF);
        memory.WriteUInt16(0x110, 1);
        memory.WriteUInt16(0x112, 0);
        memory.WriteUInt16(0x114, 0);
        memory.WriteByte(0x11C, 0xFF);
        memory.WriteByte(0x11D, 0);
        memory.WriteByte(0x11E, 0);
        memory.WriteByte(0x11F, 0xFF);
        memory.WriteUInt32(0x200, 0xFF18013F);
        memory.WriteUInt32(0x204, 0x00010000);
        memory.WriteUInt32(0x208, 0x04100000);
        memory.WriteUInt32(0x20C, 0x00000100);
        memory.WriteUInt32(0x210, 0xB5000000);
        memory.WriteUInt32(0x214, 0x00000A00);
        memory.WriteUInt32(0x218, 0xB8000000);
        memory.WriteUInt32(0x21C, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 32, 0, 0));

        Assert.Equal(1, renderer.LinesDrawn);
        Assert.Equal(0, renderer.UnsupportedCommands);
        Assert.NotEqual(0u, memory.ReadUInt32(0x10000 + ((120 * 320 + 200) * 4)));
    }

    [Fact]
    public void Fast3dRdpStateTracksBlendAndFogColorsForCompatibilityTraces()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt32(0x200, 0xEF000000);
        memory.WriteUInt32(0x204, 0x00404000);
        memory.WriteUInt32(0x208, 0xF8000000);
        memory.WriteUInt32(0x20C, 0x10203040);
        memory.WriteUInt32(0x210, 0xF9000000);
        memory.WriteUInt32(0x214, 0x50607080);
        memory.WriteUInt32(0x218, 0xFC000000);
        memory.WriteUInt32(0x21C, 0x00018600);
        memory.WriteUInt32(0x220, 0xB8000000);
        memory.WriteUInt32(0x224, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 40, 0, 0));

        var state = renderer.RdpState;
        Assert.Equal(0x00404000u, state.OtherModeLow);
        Assert.Equal(new Vector4(16 / 255f, 32 / 255f, 48 / 255f, 64 / 255f), state.FogColor);
        Assert.Equal(new Vector4(80 / 255f, 96 / 255f, 112 / 255f, 128 / 255f), state.BlendColor);
        Assert.True(state.CombinerConfigured);
    }

    [Fact]
    public void Fast3dTracksKeyConvertAndPrimitiveDepthRdpState()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt32(0x200, 0xEA123456);
        memory.WriteUInt32(0x204, 0x789ABCDE);
        memory.WriteUInt32(0x208, 0xEB000000);
        memory.WriteUInt32(0x20C, 0x10203040);
        memory.WriteUInt32(0x210, 0xECABCDEF);
        memory.WriteUInt32(0x214, 0x55667788);
        memory.WriteUInt32(0x218, 0xEE000000);
        memory.WriteUInt32(0x21C, 0x43211234);
        memory.WriteUInt32(0x220, 0xB8000000);
        memory.WriteUInt32(0x224, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 40, 0, 0));

        var state = renderer.RdpState;
        Assert.Equal(0, renderer.UnsupportedCommands);
        Assert.Equal(0xEA123456u, state.KeyGreenBlueWord0);
        Assert.Equal(0x789ABCDEu, state.KeyGreenBlueWord1);
        Assert.Equal(0x10203040u, state.KeyRedWord1);
        Assert.Equal(0xECABCDEFu, state.ConvertWord0);
        Assert.Equal(0x55667788u, state.ConvertWord1);
        Assert.Equal(0x4321, state.PrimitiveDepth);
        Assert.Equal(0x1234, state.PrimitiveDeltaDepth);
    }

    [Fact]
    public void Fast3dMarioStylePauseShadeUsesCombinerAndFramebufferBlender()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt32(0x400, 0x0000FFFF);
        memory.WriteUInt32(0x200, 0xFF180003);
        memory.WriteUInt32(0x204, 0x00000400);
        memory.WriteUInt32(0x208, 0xEF000000);
        // P=combined input, A=input alpha, M=framebuffer, B=1-A.
        memory.WriteUInt32(0x20C, 0x00404000);
        memory.WriteUInt32(0x210, 0xFC000000);
        // (0 - 0) * 0 + primitive for both colour and alpha.
        memory.WriteUInt32(0x214, 0x00018600);
        memory.WriteUInt32(0x218, 0xFA000000);
        memory.WriteUInt32(0x21C, 0xFF000080);
        memory.WriteUInt32(0x220, 0xF6000000);
        memory.WriteUInt32(0x224, 0);
        memory.WriteUInt32(0x228, 0xB8000000);
        memory.WriteUInt32(0x22C, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 48, 0, 0));

        var pixel = memory.ReadUInt32(0x400);
        Assert.InRange((pixel >> 24) & 0xFF, 126u, 129u);
        Assert.Equal(0u, (pixel >> 16) & 0xFF);
        Assert.InRange((pixel >> 8) & 0xFF, 126u, 129u);
        Assert.Equal(1, renderer.FramebufferPixelsBlended);
    }

    [Fact]
    public void Fast3dTwoCycleBlenderPreservesCombinerAlphaBetweenCycles()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt32(0x400, 0x0000FFFF);
        memory.WriteUInt32(0x200, 0xFF180000);
        memory.WriteUInt32(0x204, 0x00000400);
        // Two-cycle mode. Both blender cycles use P=pixel, A=pixel alpha,
        // M=framebuffer and B=1-A, with force blending enabled.
        memory.WriteUInt32(0x208, 0xEF100000);
        memory.WriteUInt32(0x20C, 0x00504000);
        // Cycle zero emits primitive colour/alpha; cycle one passes COMBINED.
        memory.WriteUInt32(0x210, 0xFC000000);
        memory.WriteUInt32(0x214, 0x00018600);
        memory.WriteUInt32(0x218, 0xFA000000);
        memory.WriteUInt32(0x21C, 0xFF000080);
        memory.WriteUInt32(0x220, 0xF6000000);
        memory.WriteUInt32(0x224, 0);
        memory.WriteUInt32(0x228, 0xB8000000);
        memory.WriteUInt32(0x22C, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 48, 0, 0));

        // Each cycle blends at one-half alpha. Red therefore contributes one
        // quarter and the original blue contributes three quarters.
        var pixel = memory.ReadUInt32(0x400);
        Assert.InRange((pixel >> 24) & 0xFF, 62u, 66u);
        Assert.Equal(0u, (pixel >> 16) & 0xFF);
        Assert.InRange((pixel >> 8) & 0xFF, 189u, 193u);
        Assert.Equal(0xFFu, pixel & 0xFF);
        Assert.Equal(1, renderer.FramebufferPixelsBlended);
    }

    [Fact]
    public void Fast3dOpaqueCoverageOverridesZeroVertexAlphaForBlending()
    {
        const uint alphaCoverageSelect = 0x2000;
        const uint coverageTimesAlpha = 0x1000;

        Assert.Equal(0f, Fast3dRenderer.ResolveBlenderAlpha(0, 0f));
        Assert.Equal(
            1f,
            Fast3dRenderer.ResolveBlenderAlpha(alphaCoverageSelect, 0f));
        Assert.Equal(
            0.25f,
            Fast3dRenderer.ResolveBlenderAlpha(
                alphaCoverageSelect | coverageTimesAlpha,
                0.25f));
    }

    [Fact]
    public void Fast3dTextureEdgeCoverageRejectsOnlyFullyTransparentPixels()
    {
        const uint alphaCoverageSelect = 0x2000;
        const uint coverageTimesAlpha = 0x1000;

        // Ordinary opaque modes may intentionally replace a zero vertex
        // alpha with full raster coverage. Texture-edge modes instead scale
        // coverage by the combined texel alpha and must preserve the cutout.
        Assert.True(Fast3dRenderer.HasRasterCoverage(alphaCoverageSelect, 0f));
        Assert.False(Fast3dRenderer.HasRasterCoverage(
            alphaCoverageSelect | coverageTimesAlpha,
            0f));
        Assert.True(Fast3dRenderer.HasRasterCoverage(
            alphaCoverageSelect | coverageTimesAlpha,
            1f / 255f));
    }

    [Fact]
    public void Fast3dAlphaCompareRejectsPixelsBelowBlendColorThreshold()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt32(0x400, 0x123456FF);
        memory.WriteUInt32(0x200, 0xFF180003);
        memory.WriteUInt32(0x204, 0x00000400);
        memory.WriteUInt32(0x208, 0xEF000000);
        memory.WriteUInt32(0x20C, 0x00000001);
        memory.WriteUInt32(0x210, 0xF9000000);
        memory.WriteUInt32(0x214, 0x000000C0);
        memory.WriteUInt32(0x218, 0xFC000000);
        memory.WriteUInt32(0x21C, 0x00018600);
        memory.WriteUInt32(0x220, 0xFA000000);
        memory.WriteUInt32(0x224, 0xFF000080);
        memory.WriteUInt32(0x228, 0xF6000000);
        memory.WriteUInt32(0x22C, 0);
        memory.WriteUInt32(0x230, 0xB8000000);
        memory.WriteUInt32(0x234, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 56, 0, 0));

        Assert.Equal(0x123456FFu, memory.ReadUInt32(0x400));
        Assert.Equal(1, renderer.AlphaPixelsRejected);
    }

    [Fact]
    public void Fast3dDitherAlphaCompareUsesHardwareModeThree()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt32(0x400, 0x123456FF);
        memory.WriteUInt32(0x200, 0xFF180000);
        memory.WriteUInt32(0x204, 0x00000400);
        // G_AC_DITHER occupies both alpha-compare bits (value 3). Value 2 is
        // reserved and was the value the software RDP previously decoded.
        memory.WriteUInt32(0x208, 0xEF000000);
        memory.WriteUInt32(0x20C, 0x00000003);
        memory.WriteUInt32(0x210, 0xFC000000);
        memory.WriteUInt32(0x214, 0x00018600);
        memory.WriteUInt32(0x218, 0xFA000000);
        memory.WriteUInt32(0x21C, 0xFF000000);
        memory.WriteUInt32(0x220, 0xF6000000);
        memory.WriteUInt32(0x224, 0);
        memory.WriteUInt32(0x228, 0xB8000000);
        memory.WriteUInt32(0x22C, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 48, 0, 0));

        Assert.Equal(0x123456FFu, memory.ReadUInt32(0x400));
        Assert.Equal(1, renderer.AlphaPixelsRejected);
    }

    [Fact]
    public void Fast3dSuppressedFrameParsesCommandsWithoutWritingPixels()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt32(0x1000, 0xFF100003);
        memory.WriteUInt32(0x1004, 0x00002000);
        memory.WriteUInt32(0x1008, 0xF7000000);
        memory.WriteUInt32(0x100C, 0x7C1F7C1F);
        memory.WriteUInt32(0x1010, 0xF600C00C);
        memory.WriteUInt32(0x1014, 0x00000000);
        memory.WriteUInt32(0x1018, 0xB8000000);
        memory.WriteUInt32(0x101C, 0);
        var renderer = new Fast3dRenderer(memory)
        {
            RasterizationEnabled = false
        };

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x1000, 32, 0, 0));

        Assert.Equal(0u, memory.ReadUInt16(0x2000));
        Assert.Equal(4, renderer.CommandsProcessed);
        Assert.Equal(0, renderer.FillRectanglesDrawn);
        Assert.Equal(0x2000u, renderer.ColorImageAddress);
    }

    [Fact]
    public void Fast3dTextureRectangleSamplesAnRgba16Texture()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt16(0x100, 0xF801);
        memory.WriteUInt16(0x102, 0x07C1);
        memory.WriteUInt16(0x104, 0x003F);
        memory.WriteUInt16(0x106, 0xFFFF);
        memory.WriteUInt16(0x108, 0xFFC1);
        memory.WriteUInt16(0x10A, 0xF83F);
        memory.WriteUInt16(0x10C, 0x07FF);
        memory.WriteUInt16(0x10E, 0x0001);
        memory.WriteUInt32(0x200, 0xFD100003);
        memory.WriteUInt32(0x204, 0x00000100);
        memory.WriteUInt32(0x208, 0xF5100200);
        memory.WriteUInt32(0x20C, 0);
        memory.WriteUInt32(0x210, 0xF3000000);
        memory.WriteUInt32(0x214, 0x00007800);
        memory.WriteUInt32(0x218, 0xF2000000);
        memory.WriteUInt32(0x21C, 0x0000C004);
        memory.WriteUInt32(0x220, 0xFF100003);
        memory.WriteUInt32(0x224, 0x00000400);
        memory.WriteUInt32(0x228, 0xE4010008);
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
        Assert.Equal(0x003F, memory.ReadUInt16(0x404));
        Assert.Equal(0xFFFF, memory.ReadUInt16(0x406));
        Assert.Equal(0xFFC1, memory.ReadUInt16(0x408));
        Assert.Equal(0xF83F, memory.ReadUInt16(0x40A));
        Assert.Equal(0x07FF, memory.ReadUInt16(0x40C));
        Assert.Equal(0x0001, memory.ReadUInt16(0x40E));
        Assert.Equal(1, renderer.TextureRectanglesDrawn);
    }

    [Fact]
    public void Fast3dWrappedTextureUsesMaskSpanInsteadOfClampExtent()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt16(0x100, 0xF801);
        memory.WriteUInt16(0x102, 0x07C1);
        memory.WriteUInt16(0x104, 0x003F);
        memory.WriteUInt16(0x106, 0xFFFF);
        memory.WriteUInt32(0x200, 0xFD100003);
        memory.WriteUInt32(0x204, 0x00000100);
        memory.WriteUInt32(0x208, 0xF5100200);
        memory.WriteUInt32(0x20C, 0);
        memory.WriteUInt32(0x210, 0xF3000000);
        memory.WriteUInt32(0x214, 0x00003000);
        // Render tile 0 wraps S on a four-texel (mask=2) boundary, while its
        // clamp extent deliberately describes only two texels. SH must not
        // shorten a wrapped axis; S=3 therefore samples the fourth texel.
        memory.WriteUInt32(0x218, 0xF5100200);
        memory.WriteUInt32(0x21C, 0x00000020);
        memory.WriteUInt32(0x220, 0xF2000000);
        memory.WriteUInt32(0x224, 0x00004000);
        memory.WriteUInt32(0x228, 0xFF100000);
        memory.WriteUInt32(0x22C, 0x00000400);
        memory.WriteUInt32(0x230, 0xE4004004);
        memory.WriteUInt32(0x234, 0);
        memory.WriteUInt32(0x238, 0xB3000000);
        memory.WriteUInt32(0x23C, 0x00600000);
        memory.WriteUInt32(0x240, 0xB2000000);
        memory.WriteUInt32(0x244, 0x04000400);
        memory.WriteUInt32(0x248, 0xB8000000);
        memory.WriteUInt32(0x24C, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 80, 0, 0));

        Assert.Equal(0xFFFF, memory.ReadUInt16(0x400));
        Assert.Equal(4, Fast3dRenderer.ResolveTextureSampleDimension(2, 2, false));
        Assert.Equal(2, Fast3dRenderer.ResolveTextureSampleDimension(2, 2, true));
    }

    [Fact]
    public void Fast3dOneCycleTextureRectangleExcludesRightAndBottomAtlasEdges()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt16(0x100, 0xF801);
        memory.WriteUInt16(0x102, 0x07C1);
        memory.WriteUInt16(0x104, 0x003F);
        memory.WriteUInt16(0x106, 0xFFFF);
        memory.WriteUInt32(0x200, 0xFD100003);
        memory.WriteUInt32(0x204, 0x00000100);
        memory.WriteUInt32(0x208, 0xF5100200);
        memory.WriteUInt32(0x20C, 0);
        memory.WriteUInt32(0x210, 0xF3000000);
        memory.WriteUInt32(0x214, 0x00003000);
        memory.WriteUInt32(0x218, 0xF2000000);
        memory.WriteUInt32(0x21C, 0x0000C000);
        memory.WriteUInt32(0x220, 0xFF100003);
        memory.WriteUInt32(0x224, 0x00000400);
        // One-cycle rectangle [1,2) x [0,1). The lower/right coordinates
        // point at the neighboring atlas cell and must not be rasterized.
        memory.WriteUInt32(0x228, 0xE4008004);
        memory.WriteUInt32(0x22C, 0x00004000);
        memory.WriteUInt32(0x230, 0xB3000000);
        memory.WriteUInt32(0x234, 0);
        memory.WriteUInt32(0x238, 0xB2000000);
        memory.WriteUInt32(0x23C, 0x04000400);
        memory.WriteUInt32(0x240, 0xB8000000);
        memory.WriteUInt32(0x244, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 72, 0, 0));

        Assert.Equal(0, memory.ReadUInt16(0x400));
        Assert.Equal(0xF801, memory.ReadUInt16(0x402));
        Assert.Equal(0, memory.ReadUInt16(0x404));
        Assert.Equal(0, memory.ReadUInt16(0x406));
        Assert.Equal(0, memory.ReadUInt16(0x408));
        Assert.Equal(0, memory.ReadUInt16(0x40A));
        Assert.Equal(0, memory.ReadUInt16(0x40C));
        Assert.Equal(0, memory.ReadUInt16(0x40E));
        Assert.Equal(1, renderer.TextureRectanglesDrawn);
    }

    [Fact]
    public void Fast3dTextureRectangleHonorsScissorBounds()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt16(0x100, 0xF801);
        memory.WriteUInt16(0x102, 0x07C1);
        memory.WriteUInt16(0x104, 0x003F);
        memory.WriteUInt16(0x106, 0xFFFF);
        memory.WriteUInt16(0x108, 0xFFC1);
        memory.WriteUInt16(0x10A, 0xF83F);
        memory.WriteUInt16(0x10C, 0x07FF);
        memory.WriteUInt16(0x10E, 0x0001);
        memory.WriteUInt32(0x200, 0xFD100003);
        memory.WriteUInt32(0x204, 0x00000100);
        memory.WriteUInt32(0x208, 0xF5100200);
        memory.WriteUInt32(0x20C, 0);
        memory.WriteUInt32(0x210, 0xF3000000);
        memory.WriteUInt32(0x214, 0x00007800);
        memory.WriteUInt32(0x218, 0xF2000000);
        memory.WriteUInt32(0x21C, 0x0000C004);
        memory.WriteUInt32(0x220, 0xFF100003);
        memory.WriteUInt32(0x224, 0x00000400);
        // Keep only x=[1, 3) across both rows. Quest 64 uses this same RDP
        // state to protect its visible image from HUD work at the left edge.
        memory.WriteUInt32(0x228, 0xED004000);
        memory.WriteUInt32(0x22C, 0x0000C008);
        memory.WriteUInt32(0x230, 0xE4010008);
        memory.WriteUInt32(0x234, 0);
        memory.WriteUInt32(0x238, 0xB3000000);
        memory.WriteUInt32(0x23C, 0);
        memory.WriteUInt32(0x240, 0xB2000000);
        memory.WriteUInt32(0x244, 0x04000400);
        memory.WriteUInt32(0x248, 0xB8000000);
        memory.WriteUInt32(0x24C, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 80, 0, 0));

        Assert.Equal(0, memory.ReadUInt16(0x400));
        Assert.Equal(0x07C1, memory.ReadUInt16(0x402));
        Assert.Equal(0x003F, memory.ReadUInt16(0x404));
        Assert.Equal(0, memory.ReadUInt16(0x406));
        Assert.Equal(0, memory.ReadUInt16(0x408));
        Assert.Equal(0xF83F, memory.ReadUInt16(0x40A));
        Assert.Equal(0x07FF, memory.ReadUInt16(0x40C));
        Assert.Equal(0, memory.ReadUInt16(0x40E));
        Assert.Equal(1, renderer.TextureRectanglesDrawn);
    }

    [Fact]
    public void Fast3dTextureRectangleSamplesRgba32AcrossSplitTmemBanks()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        uint[] texels =
        [
            0xFF0000FF,
            0x00FF0080,
            0x0000FFFF,
            0xFFFFFF00,
            0x11223344,
            0x55667788,
            0x99AABBCC,
            0xDDEEFF10
        ];
        for (var index = 0; index < texels.Length; index++)
        {
            memory.WriteUInt32(0x100u + (uint)(index * 4), texels[index]);
        }

        // A 4x2 RGBA32 texture. Its four-byte RDRAM texels become two bytes
        // in each of TMEM's lower/upper banks; the second row also exercises
        // the RDP's odd-row word swap via DXT.
        memory.WriteUInt32(0x200, 0xFD180003);
        memory.WriteUInt32(0x204, 0x00000100);
        memory.WriteUInt32(0x208, 0xF5180000);
        memory.WriteUInt32(0x20C, 0);
        memory.WriteUInt32(0x210, 0xF3000000);
        memory.WriteUInt32(0x214, 0x00007400);
        memory.WriteUInt32(0x218, 0xF5180200);
        memory.WriteUInt32(0x21C, 0);
        memory.WriteUInt32(0x220, 0xF2000000);
        memory.WriteUInt32(0x224, 0x0000C004);
        memory.WriteUInt32(0x228, 0xFF180003);
        memory.WriteUInt32(0x22C, 0x00000400);
        memory.WriteUInt32(0x230, 0xE4010008);
        memory.WriteUInt32(0x234, 0);
        memory.WriteUInt32(0x238, 0xB3000000);
        memory.WriteUInt32(0x23C, 0);
        memory.WriteUInt32(0x240, 0xB2000000);
        memory.WriteUInt32(0x244, 0x04000400);
        memory.WriteUInt32(0x248, 0xB8000000);
        memory.WriteUInt32(0x24C, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 80, 0, 0));

        for (var index = 0; index < texels.Length; index++)
        {
            Assert.Equal(texels[index], memory.ReadUInt32(0x400u + (uint)(index * 4)));
        }

        Assert.Equal(1, renderer.TextureRectanglesDrawn);
    }

    [Fact]
    public void Fast3dThreePointFilterUsesTheRdpTexelTriangle()
    {
        var topLeft = new Vector4(1, 0, 0, 1);
        var topRight = new Vector4(0, 1, 0, 1);
        var bottomLeft = new Vector4(0, 0, 1, 1);
        var bottomRight = new Vector4(1, 1, 1, 1);

        Assert.Equal(
            new Vector4(0.5f, 0.25f, 0.25f, 1),
            Fast3dRenderer.InterpolateThreePoint(
                topLeft,
                topRight,
                bottomLeft,
                bottomRight,
                0.25f,
                0.25f));
        Assert.Equal(
            new Vector4(0.5f, 0.75f, 0.75f, 1),
            Fast3dRenderer.InterpolateThreePoint(
                topLeft,
                topRight,
                bottomLeft,
                bottomRight,
                0.75f,
                0.75f));
    }

    [Fact]
    public void Fast3dRgba4AndRgba8ReplicateTheirComponentAcrossAllChannels()
    {
        Assert.Equal(new Vector4(10f / 15f), Fast3dRenderer.DecodeRgba4(0xA3, 0));
        Assert.Equal(new Vector4(3f / 15f), Fast3dRenderer.DecodeRgba4(0xA3, 1));
        Assert.Equal(new Vector4(128f / 255f), Fast3dRenderer.DecodeRgba8(0x80));
    }

    [Fact]
    public void Fast3dFilteredTextureCacheRefreshesAfterTmemReload()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        for (uint address = 0x100; address < 0x110; address += 2)
        {
            memory.WriteUInt16(address, 0xF801);
        }

        memory.WriteUInt32(0x200, 0xFD100003);
        memory.WriteUInt32(0x204, 0x00000100);
        memory.WriteUInt32(0x208, 0xF5100200);
        memory.WriteUInt32(0x20C, 0);
        memory.WriteUInt32(0x210, 0xF3000000);
        memory.WriteUInt32(0x214, 0x00007800);
        memory.WriteUInt32(0x218, 0xF2000000);
        memory.WriteUInt32(0x21C, 0x0000C004);
        memory.WriteUInt32(0x220, 0xBA000C02);
        memory.WriteUInt32(0x224, 0x00002000);
        memory.WriteUInt32(0x228, 0xFF100003);
        memory.WriteUInt32(0x22C, 0x00000400);
        memory.WriteUInt32(0x230, 0xE4010008);
        memory.WriteUInt32(0x234, 0);
        memory.WriteUInt32(0x238, 0xB3000000);
        memory.WriteUInt32(0x23C, 0);
        memory.WriteUInt32(0x240, 0xB2000000);
        memory.WriteUInt32(0x244, 0x04000400);
        memory.WriteUInt32(0x248, 0xB8000000);
        memory.WriteUInt32(0x24C, 0);
        var renderer = new Fast3dRenderer(memory);
        var task = new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 80, 0, 0);

        renderer.Execute(task);
        Assert.Equal(0xF801, memory.ReadUInt16(0x400));

        for (uint address = 0x100; address < 0x110; address += 2)
        {
            memory.WriteUInt16(address, 0x07C1);
        }

        renderer.Execute(task);
        Assert.Equal(0x07C1, memory.ReadUInt16(0x400));
        Assert.Equal(2, renderer.FilteredTextureCacheMisses);
        Assert.Equal(16, renderer.FilteredTextureTexelsDecoded);
    }

    [Fact]
    public void Fast3dTransparentTexelCanBlendToShadeInsteadOfDiscardingPixel()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        // An RGBA16 texel with zero alpha. G_CC_BLENDRGBFADEA uses texel
        // alpha as the colour interpolation factor, so this must produce the
        // white shade rather than leave the black framebuffer untouched.
        memory.WriteUInt16(0x100, 0x0000);
        memory.WriteUInt32(0x200, 0xFD100000);
        memory.WriteUInt32(0x204, 0x00000100);
        memory.WriteUInt32(0x208, 0xF5100200);
        memory.WriteUInt32(0x20C, 0);
        memory.WriteUInt32(0x210, 0xF3000000);
        memory.WriteUInt32(0x214, 0);
        memory.WriteUInt32(0x218, 0xF2000000);
        memory.WriteUInt32(0x21C, 0);
        memory.WriteUInt32(0x220, 0xFF100000);
        memory.WriteUInt32(0x224, 0x00000400);
        memory.WriteUInt32(0x228, 0xFB000000);
        memory.WriteUInt32(0x22C, 0xFFFFFFFF);
        // Cycle 0: (TEXEL0 - SHADE) * TEXEL0_ALPHA + SHADE;
        // alpha = ENVIRONMENT.
        memory.WriteUInt32(0x230, 0xFC147E00);
        memory.WriteUInt32(0x234, 0x40027A00);
        memory.WriteUInt32(0x238, 0xE4004004);
        memory.WriteUInt32(0x23C, 0);
        memory.WriteUInt32(0x240, 0xB3000000);
        memory.WriteUInt32(0x244, 0);
        memory.WriteUInt32(0x248, 0xB2000000);
        memory.WriteUInt32(0x24C, 0x04000400);
        memory.WriteUInt32(0x250, 0xB8000000);
        memory.WriteUInt32(0x254, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 88, 0, 0));

        Assert.Equal(0xFFFF, memory.ReadUInt16(0x400));
        Assert.Equal(1, renderer.TexturedPixelsDrawn);
    }

    [Fact]
    public void Fast3dTextureLoadCopiesRdramIntoPersistentTmem()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        memory.WriteUInt16(0x100, 0xF801);
        memory.WriteUInt16(0x102, 0x07C1);
        memory.WriteUInt16(0x104, 0x003F);
        memory.WriteUInt16(0x106, 0xFFFF);
        memory.WriteUInt16(0x108, 0xFFC1);
        memory.WriteUInt16(0x10A, 0xF83F);
        memory.WriteUInt16(0x10C, 0x07FF);
        memory.WriteUInt16(0x10E, 0x0001);
        memory.WriteUInt32(0x200, 0xFD100003);
        memory.WriteUInt32(0x204, 0x00000100);
        memory.WriteUInt32(0x208, 0xF5100000);
        memory.WriteUInt32(0x20C, 0x07000000);
        memory.WriteUInt32(0x210, 0xF3000000);
        memory.WriteUInt32(0x214, 0x07007800);
        memory.WriteUInt32(0x218, 0xB8000000);
        memory.WriteUInt32(0x21C, 0);
        memory.WriteUInt32(0x300, 0xF5100200);
        memory.WriteUInt32(0x304, 0);
        memory.WriteUInt32(0x308, 0xF2000000);
        memory.WriteUInt32(0x30C, 0x0000C004);
        memory.WriteUInt32(0x310, 0xFF100003);
        memory.WriteUInt32(0x314, 0x00000400);
        memory.WriteUInt32(0x318, 0xE4010008);
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
        Assert.Equal(0x003F, memory.ReadUInt16(0x404));
        Assert.Equal(0xFFFF, memory.ReadUInt16(0x406));
        Assert.Equal(0xFFC1, memory.ReadUInt16(0x408));
        Assert.Equal(0xF83F, memory.ReadUInt16(0x40A));
        Assert.Equal(0x07FF, memory.ReadUInt16(0x40C));
        Assert.Equal(0x0001, memory.ReadUInt16(0x40E));
        Assert.Equal(1, renderer.TextureRectanglesDrawn);
    }

    [Fact]
    public void Fast3dCombinerDetectsWhetherAColorCycleUsesTexture()
    {
        Assert.False(Fast3dRenderer.CombineUsesTexture(0xFCFFFFFF, 0xFFFE793C));
        Assert.True(Fast3dRenderer.CombineUsesTexture(0xFC100000, 0));
    }

    [Fact]
    public void Fast3dCombinerKeepsTexelZeroAndTexelOneAsDistinctInputs()
    {
        var memory = new N64Memory(N64Cartridge.FromBytes(N64TestSupport.CreateCartridgeImage()));
        // Cycle zero selects TEXEL1 in colour A. The rest of the mux is not
        // important here; this verifies that the renderer requests tile+1
        // rather than silently aliasing the secondary input to TEXEL0.
        memory.WriteUInt32(0x200, 0xFC200000);
        memory.WriteUInt32(0x204, 0);
        memory.WriteUInt32(0x208, 0xB8000000);
        memory.WriteUInt32(0x20C, 0);
        var renderer = new Fast3dRenderer(memory);

        renderer.Execute(new N64RspTask(
            1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x200, 16, 0, 0));

        Assert.True(renderer.RdpState.CombinerUsesTexel1);
        Assert.False(renderer.RdpState.CombinerUsesTexel0);
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
    public void Fast3dStrictMicrocodeCrcRecognizesFactor5RogueSquadron()
    {
        Assert.Equal(
            0xCBF43926u,
            Fast3dRenderer.ComputeStrictCrc32("123456789"u8));
        Assert.Equal(
            Fast3dRenderer.ComputeStrictCrc32([4, 3, 2, 1, 8, 7, 6, 5]),
            Fast3dRenderer.ComputeStrictWordSwappedCrc32([1, 2, 3, 4, 5, 6, 7, 8]));
        Assert.Equal(
            Fast3dRenderer.N64Microcode.F5Rogue,
            Fast3dRenderer.ClassifyMicrocode(banner: null, 0xDA51CCDB));
        Assert.Equal(
            Fast3dRenderer.N64Microcode.Fast3d,
            Fast3dRenderer.ClassifyMicrocode(banner: null, 0xDA51CCDA));
    }

    [Fact]
    public void LocalRogueSquadronUsesItsDedicatedFactor5CommandStreamWhenPresent()
    {
        var path = N64TestSupport.FindCartridges()
            .FirstOrDefault(candidate =>
                Path.GetFileName(candidate).Contains(
                    "Rogue",
                    StringComparison.OrdinalIgnoreCase));
        if (path is null)
        {
            output.WriteLine("Local Rogue Squadron target is not installed; optional Factor 5 gate skipped.");
            return;
        }

        var machine = N64Machine.Load(path);
        for (var field = 0; field < 120; field++)
        {
            machine.RunFrame();
        }

        Assert.Equal("F5Rogue", machine.Renderer.DetectedMicrocodeName);
        Assert.Equal(0xDA51CCDBu, machine.Renderer.MicrocodeCrc32);
        Assert.False(machine.Renderer.UnsupportedCommandCounts.ContainsKey(0x80));
        Assert.False(machine.Renderer.UnsupportedCommandCounts.ContainsKey(0x02));
        Assert.InRange(machine.Renderer.CommandsProcessed, 1, 100_000);
    }

    [Fact]
    public void Fast3dCullDisplayListDecodesSdkVertexRanges()
    {
        Assert.Equal(
            (2, 7),
            Fast3dRenderer.DecodeCullVertexRange(
                Fast3dRenderer.N64Microcode.F3dex,
                0xBE000004,
                0x0000000E));
        Assert.Equal(
            (1, 15),
            Fast3dRenderer.DecodeCullVertexRange(
                Fast3dRenderer.N64Microcode.Fast3d,
                0xBE000028,
                0));
    }

    [Fact]
    public void F3dexVertexCommandDecodesSdkDestinationAndCount()
    {
        // gSPVertex(address, 3, 5) under F3DEX 1.x:
        // parameter = v0 * 2, length = (count << 10) | (count * 16 - 1).
        Assert.Equal(
            (5, 3),
            Fast3dRenderer.DecodeF3dexVertexRange(0x040A0C2F));

        // Exercise the upper half of the 32-entry F3DEX vertex cache. The
        // former F3DEX2-style decoder interpreted the DMA length as slot 63.
        Assert.Equal(
            (16, 16),
            Fast3dRenderer.DecodeF3dexVertexRange(0x042040FF));
    }

    [Fact]
    public void ShadowsOfTheEmpireUsesEarlyFast3dBetaCommandLayout()
    {
        Assert.Equal(
            Fast3dRenderer.N64Microcode.F3dBeta,
            Fast3dRenderer.ClassifyMicrocode(
                banner: "RSP SW Version: 2.0D, 04-01-96",
                crc32: 0x94C4C833));
        Assert.Equal(
            Fast3dRenderer.N64Microcode.F3dBeta,
            Fast3dRenderer.ClassifyMicrocode(
                banner: "RSP SW Version: 2.0D, 04-01-96",
                crc32: 0xD17906E2));

        // F3DBETA stores v0 * 5 in bits 16-23 and a seven-bit count at 9.
        Assert.Equal(
            (6, 12),
            Fast3dRenderer.DecodeF3dBetaVertexRange(0x041E1800));
    }

    [Fact]
    public void Fast3dCullDisplayListRejectsOnlyACommonOutsidePlane()
    {
        Assert.True(Fast3dRenderer.AllVerticesShareClipPlane([1, 1 | 4, 1 | 16]));
        Assert.False(Fast3dRenderer.AllVerticesShareClipPlane([1, 2, 4]));
        Assert.False(Fast3dRenderer.AllVerticesShareClipPlane([1, 0, 1]));
        Assert.False(Fast3dRenderer.AllVerticesShareClipPlane([]));
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
    public void Fast3dDecalDepthModeOffsetsCoplanarGeometryTowardTheCamera()
    {
        const uint zModeOpaque = 0u << 10;
        const uint zModeInterpenetrating = 1u << 10;
        const uint zModeTranslucent = 2u << 10;
        const uint zModeDecal = 3u << 10;

        Assert.Equal(100f, Fast3dRenderer.ApplyDepthModeBias(zModeOpaque, 100f));
        Assert.Equal(100f, Fast3dRenderer.ApplyDepthModeBias(zModeInterpenetrating, 100f));
        Assert.Equal(100f, Fast3dRenderer.ApplyDepthModeBias(zModeTranslucent, 100f));
        Assert.Equal(97f, Fast3dRenderer.ApplyDepthModeBias(zModeDecal, 100f));
        Assert.Equal(0f, Fast3dRenderer.ApplyDepthModeBias(zModeDecal, 2f));
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
    public void LocalSuperMario64KeepsSubmittingGraphicsWhileItsTitleFaceIsGrabbedWhenPresent()
    {
        var path = N64TestSupport.FindSuperMario64();
        if (path is null)
        {
            output.WriteLine(
                "Local Super Mario 64 target is not installed; optional face-interaction gate skipped.");
            return;
        }

        var machine = N64Machine.Load(path);
        for (var field = 0; field < 600; field++)
        {
            machine.SetControllerState(1, N64ControllerState.Neutral);
            machine.RunFrame();
        }

        var graphicsBefore = machine.GraphicsTasksSubmitted;
        var audioBefore = machine.AudioTasksSubmitted;
        var videoInterruptsBefore = machine.Memory.VerticalInterruptsRaised;
        var controllerPollsBefore = machine.Memory.ControllerPolls;
        var faceStartState = machine.SaveState();
        var previousGraphics = graphicsBefore;
        var stalledFields = 0;
        var longestStall = 0;

        // The title-screen hand starts over Mario's face. Hold A to grab it
        // and sweep the stick in both directions, exercising the dynamic face
        // path that previously left the game waiting for another graphics task.
        for (var field = 0; field < 300; field++)
        {
            var stickX = field < 150 ? (sbyte)80 : (sbyte)-80;
            var stickY = field % 120 < 60 ? (sbyte)50 : (sbyte)-50;
            machine.SetControllerState(
                1,
                new N64ControllerState(N64Button.A, stickX, stickY));
            machine.RunFrame();

            var progress = machine.GraphicsTasksSubmitted - previousGraphics;
            if (progress == 0)
            {
                stalledFields++;
                longestStall = Math.Max(longestStall, stalledFields);
            }
            else
            {
                stalledFields = 0;
            }

            previousGraphics = machine.GraphicsTasksSubmitted;
        }

        var graphicsSubmitted = machine.GraphicsTasksSubmitted - graphicsBefore;
        var faceAudioSubmitted = machine.AudioTasksSubmitted - audioBefore;
        var faceVideoInterrupts = machine.Memory.VerticalInterruptsRaised - videoInterruptsBefore;
        var faceControllerPolls = machine.Memory.ControllerPolls - controllerPollsBefore;
        machine.LoadState(faceStartState);
        var neutralGraphicsBefore = machine.GraphicsTasksSubmitted;
        previousGraphics = neutralGraphicsBefore;
        stalledFields = 0;
        var neutralLongestStall = 0;
        for (var field = 0; field < 300; field++)
        {
            machine.SetControllerState(1, N64ControllerState.Neutral);
            machine.RunFrame();
            if (machine.GraphicsTasksSubmitted == previousGraphics)
            {
                stalledFields++;
                neutralLongestStall = Math.Max(neutralLongestStall, stalledFields);
            }
            else
            {
                stalledFields = 0;
            }

            previousGraphics = machine.GraphicsTasksSubmitted;
        }

        var neutralGraphicsSubmitted = machine.GraphicsTasksSubmitted - neutralGraphicsBefore;
        output.WriteLine(
            $"face interaction: gfx +{graphicsSubmitted}, audio +{faceAudioSubmitted}, " +
            $"VI +{faceVideoInterrupts}, polls +{faceControllerPolls}, " +
            $"longest gfx stall={longestStall}; neutral gfx +{neutralGraphicsSubmitted}, " +
            $"neutral longest stall={neutralLongestStall}");

        Assert.True(
            graphicsSubmitted >= 60,
            $"Only {graphicsSubmitted} graphics tasks completed while Mario's face was grabbed.");
        Assert.True(
            longestStall < 30,
            $"Pixel64 stopped receiving graphics tasks for {longestStall} consecutive fields.");
        Assert.True(faceControllerPolls >= 60);
        Assert.True(faceVideoInterrupts >= 250);
        Assert.Equal(0, machine.Cpu.UnsupportedInstructionCount);
        Assert.Equal(0, machine.Renderer.UnsupportedCommands);
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
    public void LocalSuperMario64TriangleTaskLowersToCompleteNativePacketsWhenPresent()
    {
        var path = N64TestSupport.FindSuperMario64();
        if (path is null)
        {
            output.WriteLine(
                "Local Super Mario 64 target is not installed; optional RDP lowering gate skipped.");
            return;
        }

        var machine = N64Machine.Load(path);
        N64RdpTrace? triangleTrace = null;
        for (var field = 0; field < 360 && triangleTrace is null; field++)
        {
            var trianglesBefore = machine.Renderer.TrianglesDrawn;
            machine.RequestGraphicsTaskCapture();
            machine.RunFrame();
            var capture = machine.LastGraphicsCapture;
            if (capture is null ||
                machine.Renderer.TrianglesDrawn == trianglesBefore)
            {
                continue;
            }

            var candidate = N64RdpTrace.Capture(capture);
            if (candidate.Commands.Any(command => command.Opcode == 0x0F))
            {
                triangleTrace = candidate;
            }
        }

        Assert.NotNull(triangleTrace);
        Assert.True(triangleTrace.IsComplete);
        Assert.Equal(0, triangleTrace.OmittedHlePrimitiveCommands);
        Assert.Equal(0, triangleTrace.UnsupportedSourceCommands);
        Assert.Contains(
            triangleTrace.Commands,
            command => command.Opcode == 0x0F &&
                       command.Words.Length == 44);
        output.WriteLine(
            $"Lowered {triangleTrace.Commands.Count(command => command.Opcode == 0x0F):N0} " +
            $"native triangle packet(s) in a complete {triangleTrace.Commands.Count:N0}-packet task.");
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
        var traceAudio = string.Equals(
            Environment.GetEnvironmentVariable("PIXEL64_TRACE_AUDIO"),
            "1",
            StringComparison.Ordinal);
        var audioDrainBuffer = new float[1_068];
        var audioDrainRemainder = 0;
        var audioRequestedValues = 0L;
        var audioReadValues = 0L;
        var audioNonFiniteValues = 0L;
        var audioClippedValues = 0L;
        var audioMaximumDelta = 0f;
        var previousAudioValue = 0f;
        var hasPreviousAudioValue = false;
        var maximumFields =
            int.TryParse(
                Environment.GetEnvironmentVariable("PIXEL64_TRACE_FIELDS"),
                out var requestedFields) &&
            requestedFields > 0
                ? requestedFields
                : autoplay
                    ? 6_000
                    : 600;
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
            if (traceAudio && machine.AudioTasksSubmitted > 0)
            {
                audioDrainRemainder += N64Machine.AudioSampleRate * 2;
                var requestedValues = audioDrainRemainder / 60;
                audioDrainRemainder %= 60;
                audioRequestedValues += requestedValues;
                var valuesRead = machine.ReadAudioSamples(audioDrainBuffer.AsSpan(0, requestedValues));
                audioReadValues += valuesRead;
                for (var sample = 0; sample < valuesRead; sample++)
                {
                    var value = audioDrainBuffer[sample];
                    if (!float.IsFinite(value))
                    {
                        audioNonFiniteValues++;
                        continue;
                    }

                    if (Math.Abs(value) >= 0.999f)
                    {
                        audioClippedValues++;
                    }

                    if (hasPreviousAudioValue)
                    {
                        audioMaximumDelta = Math.Max(
                            audioMaximumDelta,
                            Math.Abs(value - previousAudioValue));
                    }

                    previousAudioValue = value;
                    hasPreviousAudioValue = true;
                }
            }
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
        if (traceAudio)
        {
            output.WriteLine(
                $"audio-drain requested={audioRequestedValues} read={audioReadValues} " +
                $"short={audioRequestedValues - audioReadValues} " +
                $"buffered={machine.BufferedAudioSampleCount} " +
                $"dropped={machine.DroppedAudioSampleCount} " +
                $"non-finite={audioNonFiniteValues} clipped={audioClippedValues} " +
                $"max-delta={audioMaximumDelta:0.0000} " +
                $"ucode-unsupported={machine.AudioProcessor.UnsupportedCommands}");
        }
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
