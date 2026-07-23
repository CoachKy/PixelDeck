namespace PixelDeck.Emulation.Snes;

public sealed class SnesMachine
{
    public const int AudioSampleRate = SnesDsp.SampleRate;
    private const uint SaveStateMagic = 0x31534E50; // PNS1
    private const int SaveStateVersion = 6;
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

    public ushort ApuOutputWord => _bus.ApuOutputWord;

    public long ApuExecutedInstructions => _bus.ApuExecutedInstructions;

    public byte ApuFirstUnsupportedOpcode => _bus.ApuFirstUnsupportedOpcode;

    public ushort ApuFirstUnsupportedAddress => _bus.ApuFirstUnsupportedAddress;

    public int BufferedAudioSampleCount => _bus.BufferedAudioSampleCount;

    internal int ActiveAudioVoiceCount => _bus.ActiveAudioVoiceCount;

    internal byte ReadDspRegister(byte address) => _bus.ReadDspRegister(address);

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

    public static SnesMachine Load(string gamePath) => new(SnesCartridge.Load(gamePath));

    public byte PeekMemory(uint address) => _bus.Read(address);

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

            var cpuCycles = _cpu.Step();
            _bus.Clock(cpuCycles);
        }

        return _bus.Ppu.FrameBuffer;
    }

    public void SetControllerState(int player, SnesButton buttons) => _bus.SetControllerState(player, buttons);

    public int ReadAudioSamples(Span<float> destination) => _bus.ReadAudioSamples(destination);

    public void ClearAudioSamples() => _bus.ClearAudioSamples();

    public byte[] SaveState()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(SaveStateMagic);
        writer.Write(SaveStateVersion);
        Cartridge.SaveState(writer);
        _cpu.SaveState(writer);
        _bus.SaveState(writer);
        writer.Flush();
        return stream.ToArray();
    }

    public void LoadState(ReadOnlySpan<byte> state)
    {
        using var stream = new MemoryStream(state.ToArray(), writable: false);
        using var reader = new BinaryReader(stream);

        if (reader.ReadUInt32() != SaveStateMagic || reader.ReadInt32() != SaveStateVersion)
        {
            throw new InvalidDataException("This is not a compatible PixelDeck SNES save state.");
        }

        Cartridge.LoadState(reader);
        _cpu.LoadState(reader);
        _bus.LoadState(reader);

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("The SNES save state contains unexpected trailing data.");
        }
    }
}
