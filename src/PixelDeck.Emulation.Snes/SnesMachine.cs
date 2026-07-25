namespace PixelDeck.Emulation.Snes;

public sealed class SnesMachine
{
    public const int AudioSampleRate = SnesDsp.SampleRate;
    private const uint SaveStateMagic = 0x31534E50; // PNS1
    private const int SaveStateVersion = 13;
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

    public byte BackgroundMode => _bus.Ppu.BackgroundMode;

    public byte MainScreenLayers => _bus.Ppu.MainScreen;

    public long PpuRegisterWriteCount => _bus.Ppu.RegisterWriteCount;

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
        _bus.AdvanceMasterClocks(_cpu.LastMasterClocks);
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
            _bus.AdvanceMasterClocks(_cpu.LastMasterClocks);
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
        if (stateVersion is not (10 or 11 or 12 or SaveStateVersion))
        {
            throw new ArgumentOutOfRangeException(
                nameof(stateVersion),
                "PixelSNES can only write the current state or its v10-v12 migration fixtures.");
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
        if (stateVersion is not (10 or 11 or 12 or SaveStateVersion))
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
