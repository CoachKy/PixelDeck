using PixelDeck.Emulation.Snes;
using SkiaSharp;
using Xunit.Abstractions;

namespace PixelDeck.App.Tests;

public sealed class SnesMachineTests
{
    private readonly ITestOutputHelper _output;

    public SnesMachineTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void SyntheticLoRomRunsFramesAndRestoresState()
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var machine = SnesMachine.Load(gamePath);
            var firstFrame = machine.RunFrame().ToArray();

            Assert.Equal(256 * 224, firstFrame.Length);
            Assert.Contains(firstFrame, color => color != 0xFF000000u);
            Assert.True(machine.CpuCycles > 0);

            var state = machine.SaveState();
            var expectedAddress = machine.ProgramAddress;
            var expectedCycles = machine.CpuCycles;
            var expectedFrame = machine.RunFrame().ToArray();

            machine.LoadState(state);

            Assert.Equal(expectedAddress, machine.ProgramAddress);
            Assert.Equal(expectedCycles, machine.CpuCycles);
            Assert.Equal(expectedFrame, machine.RunFrame().ToArray());
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void PreviousStateMigratesToTheCurrentCore(int stateVersion)
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var machine = SnesMachine.Load(gamePath);
            machine.RunFrame();
            var previousState = machine.SaveState(stateVersion);
            var expectedFrame = machine.RunFrame().ToArray();

            machine.LoadState(previousState);

            Assert.Equal(expectedFrame, machine.RunFrame().ToArray());
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void EightBitAccumulatorTransfersItsFullHiddenValueToSixteenBitIndexes()
    {
        var gamePath = CreateSyntheticLoRom(
        [
            0x18,             // CLC
            0xFB,             // XCE - enter native mode
            0xC2, 0x10,       // REP #$10 - 16-bit indexes
            0xE2, 0x20,       // SEP #$20 - 8-bit accumulator
            0xA9, 0x12,       // LDA #$12
            0xEB,             // XBA - hidden high byte = $12
            0xA9, 0x34,       // LDA #$34 - full C register = $1234
            0xAA,             // TAX
            0x8E, 0x00, 0x00, // STX $0000
            0xA8,             // TAY
            0x8C, 0x02, 0x00, // STY $0002
            0xDB              // STP
        ]);

        try
        {
            var machine = SnesMachine.Load(gamePath);
            machine.RunFrame();

            Assert.Equal(0x34, machine.PeekMemory(0x0000));
            Assert.Equal(0x12, machine.PeekMemory(0x0001));
            Assert.Equal(0x34, machine.PeekMemory(0x0002));
            Assert.Equal(0x12, machine.PeekMemory(0x0003));
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void BatteryBackedSaveRamPersistsAcrossCartridgeInstances()
    {
        var gamePath = CreateSyntheticLoRom(
            cartridgeType: 0x02,
            ramSizeExponent: 0x03);
        var savePath = Path.Combine(Path.GetTempPath(), $"PixelDeck-{Guid.NewGuid():N}.sav");
        try
        {
            var first = SnesMachine.Load(gamePath, savePath);
            Assert.True(first.Cartridge.Info.HasBatteryBackedRam);
            first.Cartridge.Write(0x700123, 0x5A);
            first.FlushBatterySave();

            var second = SnesMachine.Load(gamePath, savePath);
            Assert.Equal(0x5A, second.Cartridge.Read(0x700123));
        }
        finally
        {
            File.Delete(gamePath);
            File.Delete(savePath);
            File.Delete(savePath + ".tmp");
        }
    }

    [Fact]
    public void CompleteTemporaryBatterySaveIsRecoveredAfterInterruptedCommit()
    {
        var gamePath = CreateSyntheticLoRom(
            cartridgeType: 0x02,
            ramSizeExponent: 0x03);
        var savePath = Path.Combine(Path.GetTempPath(), $"PixelDeck-{Guid.NewGuid():N}.sav");
        try
        {
            var machine = SnesMachine.Load(gamePath, savePath);
            var expectedLength = machine.Cartridge.Info.RamSize;
            var temporary = new byte[expectedLength];
            temporary[0x123] = 0xA5;
            File.WriteAllBytes(savePath + ".tmp", temporary);

            var recovered = SnesMachine.Load(gamePath, savePath);

            Assert.Equal(0xA5, recovered.Cartridge.Read(0x700123));
            Assert.True(File.Exists(savePath));
            Assert.False(File.Exists(savePath + ".tmp"));
        }
        finally
        {
            File.Delete(gamePath);
            File.Delete(savePath);
            File.Delete(savePath + ".tmp");
        }
    }

    [Fact]
    public void Dsp1CartridgeTypeIsOfferedAsCompatible()
    {
        var gamePath = CreateSyntheticLoRom(
            cartridgeType: 0x03,
            ramSizeExponent: 0x03);
        try
        {
            var info = SnesCartridge.Inspect(gamePath);

            Assert.True(info.IsSupported);
            Assert.False(info.HasBatteryBackedRam);
            Assert.Contains("DSP-1", info.CompatibilityMessage);
            Assert.NotNull(SnesCartridge.Load(gamePath));
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void HiRomDsp1BoardRoutesDataStatusAndBatteryRam()
    {
        var gamePath = CreateSyntheticHiRom(
            cartridgeType: 0x05,
            ramSizeExponent: 0x03);
        try
        {
            var cartridge = SnesCartridge.Load(gamePath);
            var bus = new SnesBus(cartridge);

            Assert.True(cartridge.Info.HasBatteryBackedRam);
            Assert.Contains("DSP-1", cartridge.Info.CompatibilityMessage);
            Assert.Equal(0x80, bus.Read(0x007000));

            bus.Write(0x006000, 0x00);
            bus.Write(0x006000, 0x00);
            bus.Write(0x006000, 0x40);
            bus.Write(0x006000, 0x00);
            bus.Write(0x006000, 0x40);

            Assert.Equal(0x00, bus.Read(0x006000));
            Assert.Equal(0x20, bus.Read(0x006000));

            cartridge.Write(0x206123, 0xA5);
            Assert.Equal(0xA5, cartridge.Read(0x206123));
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void CorruptSaveStateFailsIntegrityWithoutChangingTheRunningMachine()
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var machine = SnesMachine.Load(gamePath);
            machine.RunFrame();
            var stableState = machine.SaveState();
            machine.RunFrame();
            machine.LoadState(stableState);
            var expectedAddress = machine.ProgramAddress;
            var expectedCycles = machine.CpuCycles;
            var expectedFrame = machine.RunFrame().ToArray();
            machine.LoadState(stableState);

            var corruptState = stableState.ToArray();
            corruptState[^1] ^= 0x80;

            Assert.Throws<InvalidDataException>(() => machine.LoadState(corruptState));
            Assert.Equal(expectedAddress, machine.ProgramAddress);
            Assert.Equal(expectedCycles, machine.CpuCycles);
            Assert.Equal(expectedFrame, machine.RunFrame().ToArray());
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void HdmaChangesPpuRegistersOnSuccessiveScanlines()
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var cartridge = SnesCartridge.Load(gamePath);
            var bus = new SnesBus(cartridge);
            bus.Ppu.WriteRegister(0x2121, 0);
            bus.Ppu.WriteRegister(0x2122, 0x1F);
            bus.Ppu.WriteRegister(0x2122, 0);
            bus.Write(0x7E0000, 0x82);
            bus.Write(0x7E0001, 0x0F);
            bus.Write(0x7E0002, 0x80);
            bus.Write(0x7E0003, 0x00);
            bus.Write(0x004300, 0x00);
            bus.Write(0x004301, 0x00);
            bus.Write(0x004302, 0x00);
            bus.Write(0x004303, 0x00);
            bus.Write(0x004304, 0x7E);
            bus.Write(0x00420C, 0x01);

            bus.BeginFrame();
            bus.Clock(228);
            bus.Clock(228);

            Assert.Equal(0xFFFF0000u, bus.Ppu.FrameBuffer[0]);
            Assert.Equal(0xFF000000u, bus.Ppu.FrameBuffer[SnesPpu.Width]);
            Assert.Equal(0x04, bus.Read(0x004308));
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void AutomaticControllerRegistersUseTheHardwareButtonLayout()
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var cartridge = SnesCartridge.Load(gamePath);
            var bus = new SnesBus(cartridge);
            bus.SetControllerState(
                1,
                SnesButton.B | SnesButton.Right | SnesButton.A | SnesButton.R);
            bus.Write(0x004200, 0x01);

            bus.BeginFrame();
            bus.Clock(60_000);

            Assert.Equal(0x90, bus.Read(0x004218));
            Assert.Equal(0x81, bus.Read(0x004219));
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void AutomaticControllerReadReportsBusyUntilTheHardwareWindowEnds()
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var cartridge = SnesCartridge.Load(gamePath);
            var bus = new SnesBus(cartridge);
            bus.SetControllerState(1, SnesButton.B | SnesButton.Start);
            bus.Write(0x004200, 0x01);

            bus.BeginFrame();
            // VBlank begins after 225 1,364-master-clock scanlines.
            bus.Clock(51_150);

            Assert.Equal(1, bus.Read(0x004212) & 1);
            Assert.Equal(0, bus.Read(0x004218));
            Assert.Equal(0, bus.Read(0x004219));

            bus.Clock(703);
            Assert.Equal(1, bus.Read(0x004212) & 1);
            bus.Clock(1);

            Assert.Equal(0, bus.Read(0x004212) & 1);
            Assert.Equal(0, bus.Read(0x004218));
            Assert.Equal(0x90, bus.Read(0x004219));
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void AcknowledgedVblankDoesNotRetriggerWhenNmiIsRestored()
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var cartridge = SnesCartridge.Load(gamePath);
            var bus = new SnesBus(cartridge);

            bus.BeginFrame();
            bus.Clock(51_150);
            Assert.Equal(0x80, bus.Read(0x004210) & 0x80);

            bus.Write(0x004200, 0x00);
            bus.Write(0x004200, 0x80);

            Assert.False(bus.ConsumeNmi());

            bus = new SnesBus(cartridge);
            bus.BeginFrame();
            bus.Clock(51_150);
            bus.Write(0x004200, 0x80);

            Assert.True(bus.ConsumeNmi());
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void CpuMathUnitCompletesMultiplicationAndDivisionOverHardwareCycles()
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var cartridge = SnesCartridge.Load(gamePath);
            var bus = new SnesBus(cartridge);

            bus.Write(0x004202, 13);
            bus.Write(0x004203, 7);
            Assert.Equal(0, bus.Read(0x004216));
            bus.Clock(7);
            Assert.Equal(14, bus.Read(0x004214));
            bus.Clock(1);
            Assert.Equal(91, bus.Read(0x004216));
            Assert.Equal(0, bus.Read(0x004217));

            bus.Write(0x004204, 0xE8);
            bus.Write(0x004205, 0x03);
            bus.Write(0x004206, 7);
            Assert.Equal(0xE8, bus.Read(0x004216));
            Assert.Equal(0x03, bus.Read(0x004217));
            bus.Clock(16);

            Assert.Equal(142, bus.Read(0x004214));
            Assert.Equal(0, bus.Read(0x004215));
            Assert.Equal(6, bus.Read(0x004216));
            Assert.Equal(0, bus.Read(0x004217));
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void CpuInstructionTimingUsesBaseCyclesAndAddressSpeed()
    {
        var gamePath = CreateSyntheticLoRom([0xEA, 0xDB]); // NOP; STP
        try
        {
            var machine = SnesMachine.Load(gamePath);

            machine.StepInstructionForDiagnostics();

            Assert.Equal(2, machine.CpuCycles);
            Assert.Equal(14, machine.LastInstructionMasterClocks);
            Assert.Equal(14, machine.MasterClocks);
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void CpuInstructionTimingAddsTakenBranchAndIndexedPagePenalties()
    {
        var branchPath = CreateSyntheticLoRom([0x80, 0x00, 0xDB]); // BRA +0; STP
        var indexedPath = CreateSyntheticLoRom(
        [
            0xA2, 0x01,       // LDX #$01
            0xBD, 0xFF, 0x80, // LDA $80FF,X - crosses into $8100
            0xDB
        ]);
        try
        {
            var branchMachine = SnesMachine.Load(branchPath);
            branchMachine.StepInstructionForDiagnostics();
            Assert.Equal(3, branchMachine.CpuCycles);
            Assert.Equal(22, branchMachine.LastInstructionMasterClocks);

            var indexedMachine = SnesMachine.Load(indexedPath);
            indexedMachine.StepInstructionForDiagnostics();
            indexedMachine.StepInstructionForDiagnostics();
            Assert.Equal(7, indexedMachine.CpuCycles);
            Assert.Equal(38, indexedMachine.LastInstructionMasterClocks);
        }
        finally
        {
            File.Delete(branchPath);
            File.Delete(indexedPath);
        }
    }

    [Fact]
    public void CpuAddressSpeedsHonorMemoryMapAndFastRomControl()
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var bus = new SnesBus(SnesCartridge.Load(gamePath));

            Assert.Equal(8, bus.GetCpuAccessMasterClocks(0x000000));
            Assert.Equal(6, bus.GetCpuAccessMasterClocks(0x002000));
            Assert.Equal(12, bus.GetCpuAccessMasterClocks(0x004000));
            Assert.Equal(6, bus.GetCpuAccessMasterClocks(0x004200));
            Assert.Equal(8, bus.GetCpuAccessMasterClocks(0x006000));
            Assert.Equal(8, bus.GetCpuAccessMasterClocks(0x008000));
            Assert.Equal(8, bus.GetCpuAccessMasterClocks(0x808000));
            Assert.Equal(8, bus.GetCpuAccessMasterClocks(0xC00000));

            bus.Write(0x00420D, 0xFF);

            Assert.Equal(1, bus.MemorySpeedControl);
            Assert.Equal(8, bus.GetCpuAccessMasterClocks(0x008000));
            Assert.Equal(6, bus.GetCpuAccessMasterClocks(0x808000));
            Assert.Equal(6, bus.GetCpuAccessMasterClocks(0xC00000));
            Assert.Equal(8, bus.GetCpuAccessMasterClocks(0x7E0000));
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void GeneralDmaAddsGlobalChannelAndByteTransferStalls()
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var bus = new SnesBus(SnesCartridge.Load(gamePath));
            bus.Write(0x004300, 0x00);
            bus.Write(0x004301, 0x00);
            bus.Write(0x004302, 0x00);
            bus.Write(0x004303, 0x00);
            bus.Write(0x004304, 0x7E);
            bus.Write(0x004305, 0x03);
            bus.Write(0x004306, 0x00);

            bus.BeginCpuInstructionTiming();
            bus.CpuWrite(0x00420B, 0x01);
            var timing = bus.EndCpuInstructionTiming(1);

            Assert.Equal(1, timing.CpuCycles);
            Assert.Equal(46, timing.MasterClocks);
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void CurrentBusStateRestoresFastRomAndMasterClockPhase()
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var bus = new SnesBus(SnesCartridge.Load(gamePath));
            bus.Write(0x00420D, 0x01);
            bus.AdvanceMasterClocks(123);
            var expectedMasterClocks = bus.TotalMasterClocks;

            using var state = SaveBusState(bus, stateVersion: 13);
            bus.Write(0x00420D, 0x00);
            bus.AdvanceMasterClocks(456);
            LoadBusState(bus, state, stateVersion: 13);

            Assert.Equal(1, bus.MemorySpeedControl);
            Assert.Equal(expectedMasterClocks, bus.TotalMasterClocks);
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void BusStateRestoresInFlightMathAndAutomaticControllerTiming()
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var cartridge = SnesCartridge.Load(gamePath);
            var bus = new SnesBus(cartridge);
            bus.Write(0x004202, 13);
            bus.Write(0x004203, 7);
            bus.Clock(3);

            using var mathState = SaveBusState(bus);
            bus.Clock(5);
            Assert.Equal(91, bus.Read(0x004216));
            LoadBusState(bus, mathState);
            bus.Clock(5);
            Assert.Equal(91, bus.Read(0x004216));

            bus = new SnesBus(cartridge);
            bus.SetControllerState(1, SnesButton.B | SnesButton.Start);
            bus.Write(0x004200, 0x01);
            bus.BeginFrame();
            bus.Clock(51_150);
            bus.Clock(100);
            Assert.Equal(1, bus.Read(0x004212) & 1);

            using var controllerState = SaveBusState(bus);
            bus.Clock(604);
            Assert.Equal(0x90, bus.Read(0x004219));
            LoadBusState(bus, controllerState);
            bus.Clock(603);
            Assert.Equal(1, bus.Read(0x004212) & 1);
            bus.Clock(1);
            Assert.Equal(0x90, bus.Read(0x004219));

            using var legacyState = SaveBusState(bus, stateVersion: 10);
            bus.SetControllerState(1, SnesButton.None);
            bus.Clock(1_000);
            LoadBusState(bus, legacyState, stateVersion: 10);
            Assert.Equal(0x90, bus.Read(0x004219));
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void WrIoFallingEdgeLatchesCountersAndDisablesSoftwareRelatch()
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var cartridge = SnesCartridge.Load(gamePath);
            var bus = new SnesBus(cartridge);
            bus.BeginFrame();
            bus.Clock(17);

            bus.Write(0x004201, 0x7F);
            bus.Clock(17);
            bus.Read(0x002137);

            Assert.Equal(0x7F, bus.Read(0x004213));
            Assert.Equal(25, bus.Read(0x00213C));
            Assert.Equal(0x43, bus.Read(0x00213F));
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void AutomaticControllerRegistersRemainLatchedWhenAutoReadIsDisabled()
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var cartridge = SnesCartridge.Load(gamePath);
            var bus = new SnesBus(cartridge);
            bus.SetControllerState(1, SnesButton.Start);

            bus.BeginFrame();
            bus.Clock(60_000);

            Assert.Equal(0, bus.Read(0x004218));
            Assert.Equal(0, bus.Read(0x004219));
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void SoftwareLatchCapturesTheCurrentHorizontalAndVerticalCounters()
    {
        var gamePath = CreateSyntheticLoRom();
        try
        {
            var cartridge = SnesCartridge.Load(gamePath);
            var bus = new SnesBus(cartridge);
            bus.BeginFrame();
            bus.Clock(17);
            bus.Read(0x002137);
            bus.Clock(300);

            Assert.Equal(25, bus.Read(0x00213C));
            Assert.Equal(24, bus.Read(0x00213C));
            Assert.Equal(0, bus.Read(0x00213D));
            Assert.Equal(0, bus.Read(0x00213D));
            Assert.Equal(0x43, bus.Read(0x00213F));

            bus.Read(0x002137);
            Assert.Equal(134, bus.Read(0x00213C));
            Assert.Equal(1, bus.Read(0x00213D));
        }
        finally
        {
            File.Delete(gamePath);
        }
    }

    [Fact]
    public void LocalSnesImagesCompleteFramesWhenPresent()
    {
        var configuredGamesFolder = Environment.GetEnvironmentVariable("PIXELDECK_GAMES_FOLDER");
        var gamesFolder = string.IsNullOrWhiteSpace(configuredGamesFolder)
            ? Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "Games",
                    "SuperNintendo"))
            : Path.Combine(Path.GetFullPath(configuredGamesFolder), "SuperNintendo");
        if (!Directory.Exists(gamesFolder))
        {
            return;
        }

        var gamePaths = Directory.EnumerateFiles(gamesFolder)
            .Where(path => Path.GetExtension(path) is ".sfc" or ".smc")
            .Where(path =>
            {
                var filter = Environment.GetEnvironmentVariable("PIXELDECK_SNES_GAME");
                return string.IsNullOrWhiteSpace(filter) ||
                       Path.GetFileName(path).Contains(filter, StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
        var requestedFrameCount = int.TryParse(
            Environment.GetEnvironmentVariable("PIXELDECK_SNES_FRAMES"),
            out var parsedFrameCount)
            ? Math.Max(1, parsedFrameCount)
            : (int?)null;
        var supportedImages = 0;
        var failures = new List<string>();

        foreach (var gamePath in gamePaths)
        {
            try
            {
                var machine = SnesMachine.Load(gamePath);
                supportedImages++;
                var gameName = Path.GetFileName(gamePath);
                var frameCount = requestedFrameCount ?? RequiredBootFrames(gameName);
                uint[] frame = [];
                var maximumColors = 0;
                var visibleFrames = 0;
                var audioBuffer = new float[4096];
                long audioSamplesRead = 0;
                var audioPeak = 0f;
                var isZelda = gameName
                    .Contains("Link to the Past", StringComparison.OrdinalIgnoreCase);
                var zeldaStartFrames = 0;
                for (var index = 0; index < frameCount; index++)
                {
                    if (isZelda &&
                        zeldaStartFrames == 0 &&
                        machine.PeekMemory(0x7E0010) == 0 &&
                        machine.PeekMemory(0x7E0011) >= 8)
                    {
                        zeldaStartFrames = 4;
                    }

                    machine.SetControllerState(
                        1,
                        zeldaStartFrames > 0 || (!isZelda && index is >= 180 and < 184)
                            ? SnesButton.Start
                            : SnesButton.None);
                    if (zeldaStartFrames > 0)
                    {
                        zeldaStartFrames--;
                    }
                    frame = machine.RunFrame().ToArray();
                    int chunkRead;
                    while ((chunkRead = machine.ReadAudioSamples(audioBuffer)) > 0)
                    {
                        audioSamplesRead += chunkRead;
                        for (var sampleIndex = 0; sampleIndex < chunkRead; sampleIndex++)
                        {
                            audioPeak = Math.Max(audioPeak, Math.Abs(audioBuffer[sampleIndex]));
                        }
                    }

                    maximumColors = Math.Max(maximumColors, frame.Distinct().Count());
                    if (!machine.IsDisplayBlanked)
                    {
                        visibleFrames++;
                    }
                }

                var colors = frame.Distinct().Count();
                var dspState =
                    $"voices={machine.ActiveAudioVoiceCount}, " +
                    $"FLG=${machine.ReadDspRegister(0x6C):X2}, " +
                    $"MVOL=${machine.ReadDspRegister(0x0C):X2}/${machine.ReadDspRegister(0x1C):X2}, " +
                    $"KOFF=${machine.ReadDspRegister(0x5C):X2}, " +
                    $"DIR=${machine.ReadDspRegister(0x5D):X2}, " +
                    $"ENDX=${machine.ReadDspRegister(0x7C):X2}";
                CaptureFrameWhenRequested(gamePath, frame, machine.Width, machine.Height);
                _output.WriteLine(
                    $"{gameName}: {machine.Cartridge.Info.MapMode}, frames={frameCount}, " +
                    $"PC=${machine.ProgramAddress:X6}, colors={colors}/{maximumColors}, visible={visibleFrames}, blank={machine.IsDisplayBlanked}, " +
                    $"brightness={machine.DisplayBrightness}, mode={machine.BackgroundMode}, " +
                    $"layers=${machine.MainScreenLayers:X2}, PPU writes={machine.PpuRegisterWriteCount}, " +
                    $"APU=${machine.ApuOutputWord:X4}, " +
                    $"SPC={machine.ApuExecutedInstructions} unsupported=${machine.ApuFirstUnsupportedOpcode:X2}" +
                    $"@${machine.ApuFirstUnsupportedAddress:X4}, " +
                    $"audio={audioSamplesRead / 2} stereo frames peak={audioPeak:F4} ({dspState}), " +
                    $"VRAM={machine.NonZeroVramBytes}, CGRAM={machine.NonZeroCgramBytes}, OAM={machine.NonZeroOamBytes}, " +
                    $"NMI={machine.NmiCount}, IRQ={machine.IrqCount}, BRK={machine.BrkCount}, COP={machine.CopCount}, " +
                    $"BRK first/last=${machine.FirstBrkAddress:X6}/${machine.LastBrkAddress:X6}, " +
                    $"reset reentry={machine.ResetVectorReentryCount}");
                if (isZelda)
                {
                    _output.WriteLine(
                        $"Zelda WRAM: main={machine.PeekMemory(0x7E0010)}, " +
                        $"sub={machine.PeekMemory(0x7E0011)}, " +
                        $"subsub={machine.PeekMemory(0x7E00B0)}, " +
                        $"frame={machine.PeekMemory(0x7E001A)}, " +
                        $"intro={machine.PeekMemory(0x7E1E0A)}, " +
                        $"INIDISP=${machine.PeekMemory(0x7E0013):X2}");
                }

                if (machine.CpuCycles <= 0)
                {
                    failures.Add($"{gameName} did not execute CPU cycles.");
                }

                // A deliberately shortened collection sweep is a crash/path
                // audit, not a replacement for each game's established boot
                // gate. Only demand its full scene/audio evidence once the
                // requested frame budget reaches that game's normal gate.
                if (IsKnownSample(gameName) &&
                    frameCount >= RequiredBootFrames(gameName))
                {
                    if (machine.BrkCount != 0 ||
                        machine.CopCount != 0 ||
                        machine.ApuFirstUnsupportedAddress != ushort.MaxValue)
                    {
                        failures.Add(
                            $"{gameName} entered an invalid CPU/APU path " +
                            $"(BRK={machine.BrkCount}, COP={machine.CopCount}, " +
                            $"SPC unsupported=${machine.ApuFirstUnsupportedOpcode:X2}@${machine.ApuFirstUnsupportedAddress:X4}).");
                    }

                    var minimumColors = gameName.Contains("Mega Man X", StringComparison.OrdinalIgnoreCase) ? 3 : 8;
                    if (maximumColors < minimumColors || visibleFrames < 100)
                    {
                        failures.Add(
                            $"{gameName} did not reach its expected visible boot scene " +
                            $"(maximum colors={maximumColors}, visible frames={visibleFrames}).");
                    }

                    if (audioPeak < 0.0001f)
                    {
                        failures.Add(
                            $"{gameName} did not produce audible S-DSP samples " +
                            $"({audioSamplesRead / 2} stereo frames, peak={audioPeak:F4}).");
                    }
                }

                if (isZelda &&
                    frameCount >= 900 &&
                    (visibleFrames < 300 || machine.PeekMemory(0x7E0010) == 0))
                {
                    failures.Add(
                        $"{gameName} did not progress beyond its intro " +
                        $"(visible frames={visibleFrames}, main module={machine.PeekMemory(0x7E0010)}).");
                }
            }
            catch (Exception exception) when (exception is NotSupportedException or InvalidDataException)
            {
                continue;
            }
            catch (Exception exception)
            {
                failures.Add($"{Path.GetFileName(gamePath)} failed: {exception.Message}");
            }
        }

        if (gamePaths.Length > 0)
        {
            Assert.True(supportedImages > 0, "At least one local SNES image should use a supported standard map mode.");
            _output.WriteLine(
                $"{supportedImages} of {gamePaths.Length} local SNES images used the current supported cartridge envelope.");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void LocalSuperMarioWorldReachesControllableGameplayWhenPresent()
    {
        var configuredGamesFolder = Environment.GetEnvironmentVariable("PIXELDECK_GAMES_FOLDER");
        var gamesFolder = string.IsNullOrWhiteSpace(configuredGamesFolder)
            ? Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "Games",
                    "SuperNintendo"))
            : Path.Combine(Path.GetFullPath(configuredGamesFolder), "SuperNintendo");
        if (!Directory.Exists(gamesFolder))
        {
            return;
        }

        var gamePath = Directory.EnumerateFiles(gamesFolder)
            .FirstOrDefault(path =>
                (Path.GetExtension(path) is ".sfc" or ".smc") &&
                Path.GetFileName(path).Contains("Super Mario World (U)", StringComparison.OrdinalIgnoreCase));
        if (gamePath is null)
        {
            return;
        }

        var machine = SnesMachine.Load(gamePath);
        var gameModeTransitions = new List<string>();
        var previousGameMode = byte.MaxValue;
        var titleStartPulse = 0;
        var fileConfirmPulse = 0;
        var playerConfirmPulse = 0;
        var overworldConfirmPulse = 0;
        var titleStarted = false;
        var fileConfirmed = false;
        var playerConfirmed = false;
        var overworldConfirmed = false;
        var fileSelectFrames = 0;
        var fileSelectSkyRendered = false;
        var fileSelectCursorRendered = false;
        var playerModeFrames = 0;
        var overworldFrames = 0;
        var reachedGameplay = false;
        for (var frame = 1; frame <= 1_200; frame++)
        {
            var modeBeforeFrame = machine.PeekMemory(0x7E0100);
            if (modeBeforeFrame == 0x07 && !titleStarted)
            {
                titleStartPulse = 6;
                titleStarted = true;
            }
            else if (
                modeBeforeFrame == 0x08 &&
                fileSelectFrames >= 30 &&
                !fileConfirmed)
            {
                fileConfirmPulse = 6;
                fileConfirmed = true;
            }
            playerModeFrames = modeBeforeFrame == 0x0A ? playerModeFrames + 1 : 0;
            if (playerModeFrames >= 30 && !playerConfirmed)
            {
                playerConfirmPulse = 6;
                playerConfirmed = true;
            }

            overworldFrames = modeBeforeFrame == 0x0C ? overworldFrames + 1 : 0;
            if (overworldFrames >= 60 && !overworldConfirmed)
            {
                overworldConfirmPulse = 6;
                overworldConfirmed = true;
            }

            var input = titleStartPulse > 0
                ? SnesButton.Start
                : fileConfirmPulse > 0 || playerConfirmPulse > 0 || overworldConfirmPulse > 0
                    ? SnesButton.B
                    : SnesButton.None;
            if (titleStartPulse > 0) titleStartPulse--;
            if (fileConfirmPulse > 0) fileConfirmPulse--;
            if (playerConfirmPulse > 0) playerConfirmPulse--;
            if (overworldConfirmPulse > 0) overworldConfirmPulse--;
            machine.SetControllerState(1, input);
            machine.RunFrame();
            var gameMode = machine.PeekMemory(0x7E0100);
            if (gameMode != previousGameMode)
            {
                gameModeTransitions.Add($"{frame}:${gameMode:X2}");
                previousGameMode = gameMode;
            }

            if (gameMode == 0x08)
            {
                fileSelectFrames++;
                var currentFrame = machine.CurrentFrame;
                fileSelectSkyRendered |=
                    currentFrame[(32 * machine.Width) + 128] == 0xFF9CE6E6u;
                fileSelectCursorRendered |= ContainsSuperMarioWorldFileCursor(
                    currentFrame,
                    machine.Width);
            }

            if (gameMode == 0x14)
            {
                reachedGameplay = true;
                break;
            }
        }

        Assert.True(
            reachedGameplay,
            $"Super Mario World did not reach gameplay; modes={string.Join(",", gameModeTransitions)}.");
        Assert.False(machine.IsDisplayBlanked);
        Assert.Equal(0, machine.BrkCount);
        Assert.Equal(0, machine.CopCount);
        Assert.Equal(ushort.MaxValue, machine.ApuFirstUnsupportedAddress);
        Assert.True(fileSelectSkyRendered, "Super Mario World's file-select sky rendered black.");
        Assert.True(fileSelectCursorRendered, "Super Mario World's blinking file cursor never rendered.");
        // Super Mario World's normal in-level game mode.
        Assert.Equal(0x14, machine.PeekMemory(0x7E0100));

        // The first "level" is the Dinosaur Land opening message. The game
        // intentionally ignores dismissal input until the window has fully
        // opened ($1B89 == $50) and its intro delay ($1DF5) has expired, so
        // wait for both states before pressing B.
        var messageReady = false;
        for (var frame = 0; frame < 900; frame++)
        {
            machine.SetControllerState(1, SnesButton.None);
            machine.RunFrame();
            if (machine.PeekMemory(0x7E1426) != 0 &&
                machine.PeekMemory(0x7E1B89) == 0x50 &&
                machine.PeekMemory(0x7E1DF5) == 0)
            {
                messageReady = true;
                break;
            }
        }
        Assert.True(messageReady, "Super Mario World's opening message never became dismissible.");
        for (var frame = 0; frame < 6; frame++)
        {
            machine.SetControllerState(1, SnesButton.B);
            machine.RunFrame();
        }

        // Return to the overworld, move to the first actual stage, and enter
        // it.
        var returnedToOverworld = false;
        for (var frame = 0; frame < 600; frame++)
        {
            machine.SetControllerState(1, SnesButton.None);
            machine.RunFrame();
            if (machine.PeekMemory(0x7E0100) == 0x0C)
            {
                returnedToOverworld = true;
                break;
            }
        }
        Assert.True(
            returnedToOverworld,
            $"Super Mario World's opening message did not return to the overworld " +
            $"(mode=${machine.PeekMemory(0x7E0100):X2}, " +
            $"message=${machine.PeekMemory(0x7E1426):X2}, " +
            $"window=${machine.PeekMemory(0x7E1B89):X2}, " +
            $"intro=${machine.PeekMemory(0x7E0109):X2}, " +
            $"hold1=${machine.PeekMemory(0x7E0015):X2}, " +
            $"press1=${machine.PeekMemory(0x7E0016):X2}).");

        var overworldInteractive = false;
        for (var frame = 0; frame < 600; frame++)
        {
            machine.SetControllerState(1, SnesButton.None);
            machine.RunFrame();
            if (machine.PeekMemory(0x7E0100) == 0x0E)
            {
                overworldInteractive = true;
                break;
            }
        }
        Assert.True(overworldInteractive, "Super Mario World's overworld did not become interactive.");
        var overworldFrame = machine.CurrentFrame.ToArray();
        var overworldMario = Enumerable.Range(168, 16)
            .SelectMany(y => overworldFrame.AsSpan((y * machine.Width) + 113, 16).ToArray())
            .ToArray();
        Assert.Contains(
            0xFFEE0000u,
            overworldMario);
        Assert.Contains(
            0xFFA80000u,
            overworldMario);
        Assert.Contains(
            0xFF3C3CCFu,
            overworldMario);
        CaptureFrameWhenRequested(
            gamePath,
            overworldFrame,
            machine.Width,
            machine.Height,
            "-overworld");

        for (var frame = 0; frame < 120; frame++)
        {
            machine.SetControllerState(1, SnesButton.Right);
            machine.RunFrame();
        }
        for (var frame = 0; frame < 10; frame++)
        {
            machine.SetControllerState(1, SnesButton.None);
            machine.RunFrame();
        }
        for (var frame = 0; frame < 6; frame++)
        {
            machine.SetControllerState(1, SnesButton.B);
            machine.RunFrame();
        }

        var enteredPlayableStage = false;
        for (var frame = 0; frame < 600; frame++)
        {
            machine.SetControllerState(1, SnesButton.None);
            machine.RunFrame();
            if (machine.PeekMemory(0x7E0100) == 0x14)
            {
                enteredPlayableStage = true;
                break;
            }
        }
        Assert.True(enteredPlayableStage, "Super Mario World did not enter the selected playable stage.");
        for (var frame = 0; frame < 60; frame++)
        {
            machine.SetControllerState(1, SnesButton.None);
            machine.RunFrame();
        }

        var startingX = machine.PeekMemory(0x7E0094) |
                        (machine.PeekMemory(0x7E0095) << 8);
        for (var frame = 0; frame < 120; frame++)
        {
            machine.SetControllerState(1, SnesButton.Right);
            machine.RunFrame();
        }

        var endingX = machine.PeekMemory(0x7E0094) |
                      (machine.PeekMemory(0x7E0095) << 8);
        var currentInput = machine.PeekMemory(0x7E0015);
        CaptureFrameWhenRequested(
            gamePath,
            machine.CurrentFrame.ToArray(),
            machine.Width,
            machine.Height,
            "-gameplay");
        Assert.True(
            (currentInput & 0x01) != 0,
            $"Super Mario World did not copy Right into its controller RAM " +
            $"(RAM=${currentInput:X2}, JOY1=${machine.AutomaticControllerOne:X4}, " +
            $"NMITIMEN=${machine.NmiTimerControl:X2}, " +
            $"modes={string.Join(",", gameModeTransitions)}).");
        Assert.True(
            endingX > startingX,
            $"Super Mario World received Right=${currentInput:X2} but did not move " +
            $"(X {startingX} -> {endingX}, lock=${machine.PeekMemory(0x7E009D):X2}, " +
            $"mode=${machine.PeekMemory(0x7E0100):X2}, " +
            $"modes={string.Join(",", gameModeTransitions)}).");

        machine.SetControllerState(1, SnesButton.None);
        var gameplayState = machine.SaveState();
        var expectedNextFrame = machine.RunFrame().ToArray();
        machine.LoadState(gameplayState);
        Assert.Equal(expectedNextFrame, machine.RunFrame().ToArray());
    }

    [Fact]
    public void TraceFirstLocalSnesBrkWhenRequested()
    {
        var requestedGame = Environment.GetEnvironmentVariable("PIXELDECK_SNES_TRACE_GAME");
        if (string.IsNullOrWhiteSpace(requestedGame))
        {
            return;
        }

        var gamesFolder = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Games", "SuperNintendo"));
        var gamePath = Directory.EnumerateFiles(gamesFolder)
            .First(path => Path.GetFileName(path).Contains(requestedGame, StringComparison.OrdinalIgnoreCase));
        var machine = SnesMachine.Load(gamePath);
        var trace = new Queue<string>();
        for (var instruction = 0; instruction < 20_000_000 && machine.BrkCount == 0; instruction++)
        {
            trace.Enqueue(
                $"{machine.ProgramAddress:X6} " +
                $"A={machine.CpuAccumulator:X4} X={machine.CpuX:X4} Y={machine.CpuY:X4} " +
                $"S={machine.CpuStackPointer:X4} D={machine.CpuDirectPage:X4} " +
                $"DB={machine.CpuDataBank:X2} P={machine.CpuStatus:X2}");
            if (trace.Count > 128)
            {
                trace.Dequeue();
            }

            machine.StepInstructionForDiagnostics();
        }

        _output.WriteLine(string.Join(Environment.NewLine, trace));
        _output.WriteLine(
            $"BRK={machine.BrkCount} at ${machine.FirstBrkAddress:X6}; next=${machine.ProgramAddress:X6}");
        Assert.True(machine.BrkCount > 0, "The requested trace did not reach a BRK instruction.");
    }

    [Fact]
    public void CaptureLocalSnesProgressionWhenRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PIXELDECK_SNES_PROGRESSION"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var configuredGamesFolder = Environment.GetEnvironmentVariable("PIXELDECK_GAMES_FOLDER");
        var gamesFolder = string.IsNullOrWhiteSpace(configuredGamesFolder)
            ? Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "Games",
                    "SuperNintendo"))
            : Path.Combine(Path.GetFullPath(configuredGamesFolder), "SuperNintendo");
        var requestedFrames = int.TryParse(
            Environment.GetEnvironmentVariable("PIXELDECK_SNES_FRAMES"),
            out var parsedFrames)
            ? Math.Max(600, parsedFrames)
            : 3_600;
        var gameFilter = Environment.GetEnvironmentVariable("PIXELDECK_SNES_GAME");
        var noInput = string.Equals(
            Environment.GetEnvironmentVariable("PIXELDECK_SNES_NO_INPUT"),
            "1",
            StringComparison.Ordinal);
        var checkpoints = new HashSet<int>(
            new[] { 600, 1_200, 2_400, requestedFrames }
                .Where(frame => frame <= requestedFrames));

        foreach (var gamePath in Directory.EnumerateFiles(gamesFolder)
                     .Where(path => Path.GetExtension(path) is ".sfc" or ".smc")
                     .Where(path =>
                         string.IsNullOrWhiteSpace(gameFilter) ||
                         Path.GetFileName(path).Contains(gameFilter, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            SnesMachine machine;
            try
            {
                machine = SnesMachine.Load(gamePath);
            }
            catch (Exception exception) when (exception is NotSupportedException or InvalidDataException)
            {
                _output.WriteLine($"Excluded {Path.GetFileName(gamePath)}: {exception.Message}");
                continue;
            }

            for (var frame = 1; frame <= requestedFrames; frame++)
            {
                machine.SetControllerState(
                    1,
                    noInput
                        ? SnesButton.None
                        : GetProgressionInput(Path.GetFileName(gamePath), frame));
                var pixels = machine.RunFrame();
                if (checkpoints.Contains(frame))
                {
                    CaptureFrameWhenRequested(
                        gamePath,
                        pixels.ToArray(),
                        machine.Width,
                        machine.Height,
                        $"-{frame}");
                }
            }

            var dspCommands = Enumerable.Range(0, 256)
                .Select(command => (Command: command, Count: machine.ReadDsp1CommandCount((byte)command)))
                .Where(entry => entry.Count != 0)
                .Select(entry => $"${entry.Command:X2}={entry.Count}");
            var cx4Commands = Enumerable.Range(0, 256)
                .Select(command => (Command: command, Count: machine.ReadCx4CommandCount((byte)command)))
                .Where(entry => entry.Count != 0)
                .Select(entry => $"${entry.Command:X2}={entry.Count}");
            _output.WriteLine(
                $"{Path.GetFileName(gamePath)}: {requestedFrames} frames, " +
                $"PC=${machine.ProgramAddress:X6}, NMI={machine.NmiCount}, IRQ={machine.IrqCount}, " +
                $"BRK={machine.BrkCount}, blank={machine.IsDisplayBlanked}, " +
                $"colors={machine.CurrentFrame.ToArray().Distinct().Count()}, " +
                $"DSP-1=[{string.Join(", ", dspCommands)}], " +
                $"CX4=[{string.Join(", ", cx4Commands)}].");
        }
    }

    private static SnesButton GetProgressionInput(string gameName, int frame)
    {
        if (gameName.Contains("Super Mario Kart", StringComparison.OrdinalIgnoreCase))
        {
            if (frame is >= 700 and < 706) return SnesButton.Start;
            if (frame is >= 1_000 and < 1_006 or
                >= 1_300 and < 1_306 or
                >= 1_800 and < 1_806 or
                >= 2_300 and < 2_306 or
                >= 2_800 and < 2_806)
            {
                return SnesButton.B;
            }

            return SnesButton.None;
        }

        var pulse = frame % 300;
        if (pulse is >= 30 and < 36)
        {
            return SnesButton.Start;
        }

        if (pulse is >= 90 and < 96)
        {
            return SnesButton.B;
        }

        if (pulse is >= 150 and < 156)
        {
            return SnesButton.A;
        }

        return (frame % 1_200) switch
        {
            >= 300 and < 420 => SnesButton.Right | SnesButton.B,
            >= 600 and < 720 => SnesButton.Down,
            >= 900 and < 1_020 => SnesButton.Left | SnesButton.Y,
            _ => SnesButton.None
        };
    }

    private static int RequiredBootFrames(string gameName) =>
        gameName.Contains("Donkey Kong Country", StringComparison.OrdinalIgnoreCase) ||
        gameName.Contains("Final Fantasy III", StringComparison.OrdinalIgnoreCase) ||
        gameName.Contains("Link to the Past", StringComparison.OrdinalIgnoreCase)
            ? 1_200
            : 600;

    private static bool IsKnownSample(string gameName) =>
        gameName.Contains("chrono", StringComparison.OrdinalIgnoreCase) ||
        gameName.Contains("Donkey Kong Country", StringComparison.OrdinalIgnoreCase) ||
        gameName.Contains("Final Fantasy III", StringComparison.OrdinalIgnoreCase) ||
        gameName.Contains("Link to the Past", StringComparison.OrdinalIgnoreCase) ||
        gameName.Contains("Mega Man X", StringComparison.OrdinalIgnoreCase) ||
        gameName.Contains("Super Mario Kart", StringComparison.OrdinalIgnoreCase) ||
        gameName.Contains("Super Mario World", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsSuperMarioWorldFileCursor(
        ReadOnlySpan<uint> frame,
        int width)
    {
        for (var y = 105; y < 155; y++)
        {
            for (var x = 45; x < 80; x++)
            {
                var color = frame[(y * width) + x];
                if (((color >> 16) & 0xFF) >= 0xC0 &&
                    ((color >> 8) & 0xFF) < 0x70 &&
                    (color & 0xFF) < 0x70)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static MemoryStream SaveBusState(
        SnesBus bus,
        int stateVersion = 13)
    {
        var state = new MemoryStream();
        using (var writer = new BinaryWriter(
                   state,
                   System.Text.Encoding.UTF8,
                   leaveOpen: true))
        {
            bus.SaveState(writer, stateVersion);
        }

        state.Position = 0;
        return state;
    }

    private static void LoadBusState(
        SnesBus bus,
        MemoryStream state,
        int stateVersion = 13)
    {
        state.Position = 0;
        using var reader = new BinaryReader(
            state,
            System.Text.Encoding.UTF8,
            leaveOpen: true);
        bus.LoadState(reader, stateVersion);
        Assert.Equal(state.Length, state.Position);
    }

    private static string CreateSyntheticLoRom(
        byte[]? program = null,
        byte cartridgeType = 0x00,
        byte ramSizeExponent = 0x00)
    {
        var image = new byte[32 * 1024];
        program ??=
        [
            0x78,             // SEI
            0xA9, 0x0F,       // LDA #$0F
            0x8D, 0x00, 0x21, // STA $2100 - display on, full brightness
            0xA9, 0x1F,       // LDA #$1F
            0x8D, 0x22, 0x21, // STA $2122 - backdrop red low byte
            0xA9, 0x00,
            0x8D, 0x22, 0x21, // STA $2122 - backdrop red high byte
            0xDB              // STP
        ];
        program.CopyTo(image, 0);

        const int header = 0x7FC0;
        "PIXELDECK SNES TEST  ".Select(character => (byte)character).ToArray().CopyTo(image, header);
        image[header + 0x15] = 0x20;
        image[header + 0x16] = cartridgeType;
        image[header + 0x17] = 0x05;
        image[header + 0x18] = ramSizeExponent;
        image[header + 0x19] = 0x01;
        image[header + 0x1C] = 0xCB;
        image[header + 0x1D] = 0xED;
        image[header + 0x1E] = 0x34;
        image[header + 0x1F] = 0x12;
        image[header + 0x3C] = 0x00;
        image[header + 0x3D] = 0x80;

        var path = Path.Combine(Path.GetTempPath(), $"PixelDeck-{Guid.NewGuid():N}.sfc");
        File.WriteAllBytes(path, image);
        return path;
    }

    private static string CreateSyntheticHiRom(
        byte cartridgeType,
        byte ramSizeExponent)
    {
        var image = new byte[64 * 1024];
        byte[] program =
        [
            0x78, // SEI
            0xDB  // STP
        ];
        program.CopyTo(image, 0x8000);

        const int header = 0xFFC0;
        "PIXELDECK DSP1 TEST  ".Select(character => (byte)character).ToArray().CopyTo(image, header);
        image[header + 0x15] = 0x31;
        image[header + 0x16] = cartridgeType;
        image[header + 0x17] = 0x06;
        image[header + 0x18] = ramSizeExponent;
        image[header + 0x19] = 0x01;
        image[header + 0x1C] = 0xCB;
        image[header + 0x1D] = 0xED;
        image[header + 0x1E] = 0x34;
        image[header + 0x1F] = 0x12;
        image[header + 0x3C] = 0x00;
        image[header + 0x3D] = 0x80;

        var path = Path.Combine(Path.GetTempPath(), $"PixelDeck-{Guid.NewGuid():N}.sfc");
        File.WriteAllBytes(path, image);
        return path;
    }

    private static void CaptureFrameWhenRequested(
        string gamePath,
        uint[] frame,
        int width,
        int height,
        string suffix = "")
    {
        var captureFolder = Environment.GetEnvironmentVariable("PIXELDECK_CAPTURE_SNES");
        if (string.IsNullOrWhiteSpace(captureFolder))
        {
            return;
        }

        Directory.CreateDirectory(captureFolder);
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = frame[(y * width) + x];
                bitmap.SetPixel(
                    x,
                    y,
                    new SKColor(
                        (byte)(pixel >> 16),
                        (byte)(pixel >> 8),
                        (byte)pixel,
                        0xFF));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var fileName = Path.GetFileNameWithoutExtension(gamePath) + suffix + ".png";
        using var stream = File.Create(Path.Combine(captureFolder, fileName));
        data.SaveTo(stream);
    }
}
