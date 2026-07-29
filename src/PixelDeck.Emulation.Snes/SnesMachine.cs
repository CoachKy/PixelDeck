namespace PixelDeck.Emulation.Snes;

public sealed class SnesMachine
{
    public const int AudioSampleRate = SnesDsp.SampleRate;
    private const uint SaveStateMagic = 0x31534E50; // PNS1
    private const int SaveStateVersion = 15;
    private const int SaveStateChecksumLength = 32;
    private const int MaximumSaveStatePayloadLength = 16 * 1_024 * 1_024;
    private readonly SnesBus _bus;
    private readonly Cpu65816 _cpu;

    private SnesMachine(SnesCartridge cartridge)
    {
        Cartridge = cartridge;
        _bus = new SnesBus(cartridge);
        _cpu = new Cpu65816(_bus);
        _cpu.Reset();
    }

    public SnesCartridge Cartridge { get; }

    public int Width => SnesPpu.Width;

    public int Height => SnesPpu.Height;

    public double FramesPerSecond => Cartridge.Info.IsPal ? 50.0069 : 60.0988;

    public long CpuCycles => _cpu.TotalCycles;

    public uint ProgramAddress => _cpu.ProgramAddress;

    public bool IsDisplayBlanked => _bus.Ppu.ForcedBlank;

    public byte DisplayBrightness => _bus.Ppu.Brightness;

    public long DisplayControlWrites => _bus.Ppu.DisplayControlWrites;

    /// <summary>Diagnostics for a stalled scene transition: what the game is
    /// polling while it holds the screen dark.</summary>
    public long HvbJoyReads => _bus.HvbJoyReads;

    public long HvbJoyAutoReadBusyReads => _bus.HvbJoyAutoReadBusyReads;

    public long CounterLatchCount => _bus.CounterLatchCount;

    public long DmaTransferCount => _bus.DmaTransferCount;

    public long HdmaEnableWrites => _bus.HdmaEnableWrites;

    public byte LastDmaChannelMask => _bus.LastDmaChannelMask;

    /// <summary>SA-1 coprocessor diagnostics; zero when the cartridge has none.</summary>
    public bool HasSa1 => _bus.HasSa1;

    public long Sa1ExecutedInstructions => _bus.Sa1ExecutedInstructions;

    public uint Sa1ProgramAddress => _bus.Sa1ProgramAddress;

    public byte Sa1ControlRegister => _bus.Sa1ControlRegister;

    public long Sa1DmaCount => _bus.Sa1DmaCount;

    internal Sa1Snapshot Sa1State => _bus.Sa1State;

    /// <summary>S-DD1 diagnostics; zero when the cartridge has no such chip.</summary>
    public bool HasSdd1 => _bus.HasSdd1;

    public long Sdd1DecompressionCount => _bus.Sdd1DecompressionCount;

    internal long Sdd1CandidateTransfers => _bus.Sdd1CandidateTransfers;

    internal long[] HdmaWritesByRegister => _bus.HdmaWritesByRegister;

    internal string Sdd1HeaderSummary => _bus.Sdd1HeaderSummary;

    internal IReadOnlyList<(uint Source, byte Header)> Sdd1Runs => _bus.Sdd1Runs;

    /// <summary>Super FX diagnostics; inert when the cartridge has no GSU.</summary>
    public bool HasSuperFx => _bus.HasSuperFx;

    public long SuperFxExecutedInstructions => _bus.SuperFxExecutedInstructions;

    public bool SuperFxRunning => _bus.SuperFxRunning;

    public ushort SuperFxProgramCounter => _bus.SuperFxProgramCounter;

    internal string SuperFxDiagnostics => _bus.SuperFxDiagnostics;

    internal IReadOnlyList<string> SuperFxWatchSamples => _bus.SuperFxWatchSamples;

    public long NonZeroBrightnessWrites => _bus.Ppu.NonZeroBrightnessWrites;

    public byte LastDisplayControlValue => _bus.Ppu.LastDisplayControlValue;

    public byte BackgroundMode => _bus.Ppu.BackgroundMode;

    public byte MainScreenLayers => _bus.Ppu.MainScreen;

    public long PpuRegisterWriteCount => _bus.Ppu.RegisterWriteCount;

    internal long VerticalIrqScanlinesArmed => _bus.VerticalIrqScanlinesArmed;

    internal long VerticalIrqMatches => _bus.VerticalIrqMatches;

    internal long VblankCount => _bus.VblankCount;

    internal long VblankNmiArmed => _bus.VblankNmiArmed;

    internal long RdnmiReads => _bus.RdnmiReads;

    internal long RdnmiReadsWithFlagSet => _bus.RdnmiReadsWithFlagSet;

    internal long RdnmiReadsWhileNmiEnabled => _bus.RdnmiReadsWhileNmiEnabled;

    public long SpriteRangeOverLines => _bus.Ppu.SpriteRangeOverLines;

    public long SpriteTimeOverTiles => _bus.Ppu.SpriteTimeOverTiles;

    public ushort CpuAccumulator => _cpu.Accumulator;

    public ushort CpuX => _cpu.X;

    public ushort CpuY => _cpu.Y;

    public ushort CpuDirectPage => _cpu.DirectPage;

    public byte CpuDataBank => _cpu.DataBank;

    public byte CpuStatus => _cpu.Status;

    public ushort CpuStackPointer => _cpu.StackPointer;

    public ushort ApuOutputWord => _bus.ApuOutputWord;

    public long ApuExecutedInstructions => _bus.ApuExecutedInstructions;

    public byte ApuFirstUnsupportedOpcode => _bus.ApuFirstUnsupportedOpcode;

    public ushort ApuFirstUnsupportedAddress => _bus.ApuFirstUnsupportedAddress;

    public int BufferedAudioSampleCount => _bus.BufferedAudioSampleCount;

    public long DroppedAudioSampleCount => _bus.DroppedAudioSampleCount;

    public long ReadDsp1CommandCount(byte command) => _bus.ReadDsp1CommandCount(command);

    public long ReadCx4CommandCount(byte command) => _bus.ReadCx4CommandCount(command);

    internal int ActiveAudioVoiceCount => _bus.ActiveAudioVoiceCount;

    internal byte ReadDspRegister(byte address) => _bus.ReadDspRegister(address);

    internal ushort AutomaticControllerOne => _bus.AutomaticControllerOne;

    internal byte NmiTimerControl => _bus.NmiTimerControl;

    internal byte MemorySpeedControl => _bus.MemorySpeedControl;

    internal int LastInstructionMasterClocks => _cpu.LastMasterClocks;

    internal long MasterClocks => _bus.TotalMasterClocks;

    public int NonZeroVramBytes => _bus.Ppu.NonZeroVramBytes;

    public int NonZeroCgramBytes => _bus.Ppu.NonZeroCgramBytes;

    public int NonZeroOamBytes => _bus.Ppu.NonZeroOamBytes;

    public long NmiCount => _cpu.NmiCount;

    public long IrqCount => _cpu.IrqCount;

    /// <summary>$4209/$420A V-count IRQ target scanline.</summary>
    public ushort VerticalIrqTarget => _bus.VerticalIrqTarget;

    /// <summary>$4207/$4208 H-count IRQ target dot.</summary>
    public ushort HorizontalIrqTarget => _bus.HorizontalIrqTarget;

    public int CurrentScanline => _bus.CurrentScanline;

    public long BrkCount => _cpu.BrkCount;

    public long CopCount => _cpu.CopCount;

    public long ResetVectorReentryCount => _cpu.ResetVectorReentryCount;

    public uint FirstBrkAddress => _cpu.FirstBrkAddress;

    public uint LastBrkAddress => _cpu.LastBrkAddress;

    public ReadOnlySpan<uint> CurrentFrame => _bus.Ppu.FrameBuffer;

    public static SnesMachine Load(string gamePath, string? batterySavePath = null) =>
        new(SnesCartridge.Load(gamePath, batterySavePath));

    public byte PeekMemory(uint address) => _bus.Peek(address);

    internal void StepInstructionForDiagnostics()
    {
        _cpu.Step();
        _bus.AdvanceMasterClocks(
            _cpu.LastMasterClocks,
            completesCpuInstruction: true);
    }

    public ReadOnlySpan<uint> RunFrame()
    {
        _bus.BeginFrame();
        var instructionBudget = 2_000_000;

        while (!_bus.FrameReady)
        {
            if (--instructionBudget == 0)
            {
                throw new InvalidOperationException("The SNES CPU did not complete a video frame within the safety limit.");
            }

            _cpu.Step();
            _bus.AdvanceMasterClocks(
                _cpu.LastMasterClocks,
                completesCpuInstruction: true);
        }

        return _bus.Ppu.FrameBuffer;
    }

    public void SetControllerState(int player, SnesButton buttons) => _bus.SetControllerState(player, buttons);

    public int ReadAudioSamples(Span<float> destination) => _bus.ReadAudioSamples(destination);

    public void ClearAudioSamples() => _bus.ClearAudioSamples();

    public void FlushBatterySave() => Cartridge.FlushBatterySave();

    public void Reset() => _cpu.Reset();

    public byte[] SaveState() => SaveState(SaveStateVersion);

    internal byte[] SaveState(int stateVersion)
    {
        if (stateVersion is < 10 or > SaveStateVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stateVersion),
                "PixelSNES can only write the current state or its v10-v14 migration fixtures.");
        }

        using var payloadStream = new MemoryStream();
        using (var payloadWriter = new BinaryWriter(
                   payloadStream,
                   System.Text.Encoding.UTF8,
                   leaveOpen: true))
        {
            Cartridge.SaveState(payloadWriter);
            _cpu.SaveState(payloadWriter);
            _bus.SaveState(payloadWriter, stateVersion);
            payloadWriter.Flush();
        }

        var payload = payloadStream.ToArray();
        var checksum = System.Security.Cryptography.SHA256.HashData(payload);
        using var stream = new MemoryStream(payload.Length + SaveStateChecksumLength + 12);
        using var writer = new BinaryWriter(stream);
        writer.Write(SaveStateMagic);
        writer.Write(stateVersion);
        writer.Write(payload.Length);
        writer.Write(payload);
        writer.Write(checksum);
        writer.Flush();
        return stream.ToArray();
    }

    public void LoadState(ReadOnlySpan<byte> state)
    {
        var rollbackState = SaveState();
        try
        {
            LoadStateCore(state);
        }
        catch
        {
            LoadStateCore(rollbackState);
            throw;
        }
    }

    private void LoadStateCore(ReadOnlySpan<byte> state)
    {
        using var stream = new MemoryStream(state.ToArray(), writable: false);
        using var reader = new BinaryReader(stream);

        if (reader.ReadUInt32() != SaveStateMagic)
        {
            throw new InvalidDataException("This is not a compatible PixelDeck SNES save state.");
        }

        var stateVersion = reader.ReadInt32();
        if (stateVersion is < 10 or > SaveStateVersion)
        {
            throw new InvalidDataException("This is not a compatible PixelDeck SNES save state.");
        }

        var payloadLength = reader.ReadInt32();
        if (payloadLength <= 0 || payloadLength > MaximumSaveStatePayloadLength)
        {
            throw new InvalidDataException("The PixelSNES save-state payload length is invalid.");
        }

        var expectedFileLength = 12L + payloadLength + SaveStateChecksumLength;
        if (stream.Length != expectedFileLength)
        {
            throw new InvalidDataException(
                "The PixelSNES save state is truncated or contains unexpected trailing data.");
        }

        var payload = reader.ReadBytes(payloadLength);
        var expectedChecksum = reader.ReadBytes(SaveStateChecksumLength);
        if (payload.Length != payloadLength ||
            expectedChecksum.Length != SaveStateChecksumLength ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Security.Cryptography.SHA256.HashData(payload),
                expectedChecksum))
        {
            throw new InvalidDataException("The PixelSNES save state failed its integrity check.");
        }

        using var payloadStream = new MemoryStream(payload, writable: false);
        using var payloadReader = new BinaryReader(payloadStream);
        Cartridge.LoadState(payloadReader);
        _cpu.LoadState(payloadReader);
        _bus.LoadState(payloadReader, stateVersion);
        if (payloadStream.Position != payloadStream.Length)
        {
            throw new InvalidDataException(
                "The PixelSNES save-state payload contains unexpected trailing data.");
        }
    }
}
