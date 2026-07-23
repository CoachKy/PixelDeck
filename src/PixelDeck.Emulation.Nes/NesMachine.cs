namespace PixelDeck.Emulation.Nes;

public sealed class NesMachine
{
    public const int AudioSampleRate = 48_000;
    private const uint SaveStateMagic = 0x31534450; // PDS1
    private const int SaveStateVersion = 16;
    private const int SaveStateChecksumLength = 32;
    private const int MaximumSaveStatePayloadLength = 16 * 1_024 * 1_024;
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
        using var payloadStream = new MemoryStream();
        using (var payloadWriter = new BinaryWriter(payloadStream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            Cartridge.SaveState(payloadWriter);
            _cpu.SaveState(payloadWriter);
            _bus.SaveState(payloadWriter);
            payloadWriter.Flush();
        }

        var payload = payloadStream.ToArray();
        var checksum = System.Security.Cryptography.SHA256.HashData(payload);
        using var stream = new MemoryStream(payload.Length + SaveStateChecksumLength + 12);
        using var writer = new BinaryWriter(stream);
        writer.Write(SaveStateMagic);
        writer.Write(SaveStateVersion);
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

        if (reader.ReadUInt32() != SaveStateMagic || reader.ReadInt32() != SaveStateVersion)
        {
            throw new InvalidDataException("This is not a compatible PixelDeck save state.");
        }

        var payloadLength = reader.ReadInt32();
        if (payloadLength <= 0 || payloadLength > MaximumSaveStatePayloadLength)
        {
            throw new InvalidDataException("The PixelNES save-state payload length is invalid.");
        }

        var expectedFileLength = 12L + payloadLength + SaveStateChecksumLength;
        if (stream.Length != expectedFileLength)
        {
            throw new InvalidDataException("The PixelNES save state is truncated or contains unexpected trailing data.");
        }

        var payload = reader.ReadBytes(payloadLength);
        var expectedChecksum = reader.ReadBytes(SaveStateChecksumLength);
        if (payload.Length != payloadLength ||
            expectedChecksum.Length != SaveStateChecksumLength ||
            !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Security.Cryptography.SHA256.HashData(payload),
                expectedChecksum))
        {
            throw new InvalidDataException("The PixelNES save state failed its integrity check.");
        }

        using var payloadStream = new MemoryStream(payload, writable: false);
        using var payloadReader = new BinaryReader(payloadStream);
        Cartridge.LoadState(payloadReader);
        _cpu.LoadState(payloadReader);
        _bus.LoadState(payloadReader);
        if (payloadStream.Position != payloadStream.Length)
        {
            throw new InvalidDataException("The PixelNES save-state payload contains unexpected trailing data.");
        }
    }
}
