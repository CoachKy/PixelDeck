namespace PixelDeck.Emulation.Nes;

public sealed class NesMachine
{
    public const int AudioSampleRate = 48_000;
    private const uint SaveStateMagic = 0x31534450; // PDS1
    private const int SaveStateVersion = 15;
    private readonly NesBus _bus;
    private readonly Cpu6502 _cpu;

    private NesMachine(Cartridge cartridge, NesEmulationOptions options)
    {
        Cartridge = cartridge;
        _bus = new NesBus(cartridge, options);
        _cpu = new Cpu6502(_bus);
        _bus.Apu.Reset(softReset: false);
        _bus.Apu.Clock(2);
        _cpu.Reset();
    }

    public Cartridge Cartridge { get; }

    public int Width => NesPpu.Width;

    public int Height => NesPpu.Height;

    public long CpuCycles => _cpu.TotalCycles;

    public ushort ProgramCounter => _cpu.ProgramCounter;

    public ReadOnlySpan<uint> CurrentFrame => _bus.Ppu.FrameBuffer;

    public static NesMachine Load(
        string gamePath,
        string? batterySavePath = null,
        NesEmulationOptions? options = null)
    {
        var resolvedOptions = options ?? new NesEmulationOptions();
        return new(
            Cartridge.Load(gamePath, batterySavePath, resolvedOptions.Mmc3IrqRevision),
            resolvedOptions);
    }

    public ReadOnlySpan<uint> RunFrame()
    {
        _bus.Ppu.BeginFrame();
        var instructionBudget = 1_000_000;

        while (!_bus.Ppu.FrameReady)
        {
            if (--instructionBudget == 0)
            {
                throw new InvalidOperationException("The NES CPU did not complete a video frame within the safety limit.");
            }

            _cpu.Step();
        }

        return _bus.Ppu.FrameBuffer;
    }

    public void SetControllerState(int player, NesButton buttons) => _bus.SetControllerState(player, buttons);

    public int ReadAudioSamples(Span<float> destination) => _bus.Apu.ReadSamples(destination);

    public void ClearAudioSamples() => _bus.Apu.ClearSamples();

    public void Reset()
    {
        _bus.Apu.Reset(softReset: true);
        _bus.Apu.Clock(2);
        _cpu.SoftReset();
    }

    public int BufferedAudioSampleCount => _bus.Apu.BufferedSampleCount;

    public long DroppedAudioSampleCount => _bus.Apu.DroppedSampleCount;

    public void FlushBatterySave() => Cartridge.FlushBatterySave();

    internal byte PeekCpuMemory(ushort address) => _bus.Read(address);

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
            throw new InvalidDataException("This is not a compatible PixelDeck save state.");
        }

        Cartridge.LoadState(reader);
        _cpu.LoadState(reader);
        _bus.LoadState(reader);

        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("The save state contains unexpected trailing data.");
        }
    }
}
