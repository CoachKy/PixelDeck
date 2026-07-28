using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace PixelDeck.Emulation.N64;

public sealed class N64Machine
{
    private const int StateVersion = 8;
    private static readonly byte[] StateMagic = "P64STATE"u8.ToArray();

    /// <summary>
    /// The video interface can be programmed for anything up to a 640x480
    /// progressive image, so the frame buffer is allocated for the maximum
    /// and <see cref="Width"/>/<see cref="Height"/> report the live size.
    /// </summary>
    public const int MaximumWidth = N64Memory.MaximumVideoWidth;

    public const int MaximumHeight = N64Memory.MaximumVideoHeight;

    private readonly uint[] _frame = new uint[MaximumWidth * MaximumHeight];
    private readonly byte[] _cartridgeIdentity;
    private readonly string? _savePath;
    private readonly Fast3dRenderer _renderer;
    [SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "This is the intentional graphics-backend boundary for future conformant RDP implementations.")]
    private readonly IN64GraphicsBackend _graphicsBackend;
    private readonly N64AudioProcessor _audioProcessor;
    private bool _executeGraphicsTasks = true;

    private N64Machine(N64Cartridge cartridge, string? savePath)
    {
        Cartridge = cartridge;
        Memory = new N64Memory(cartridge);
        _renderer = new Fast3dRenderer(Memory);
        _graphicsBackend = _renderer;
        _audioProcessor = new N64AudioProcessor(Memory);
        Cpu = new Vr4300Cpu(Memory, cartridge.Cic, cartridge.VideoRegion);
        _cartridgeIdentity = SHA256.HashData(cartridge.Rom);
        _savePath = savePath;
        LoadBatterySave();
    }

    public const double NtscFramesPerSecond = 60.0;

    /// <summary>
    /// The nominal audio output rate. Pixel64's verified target (Super Mario
    /// 64) programs the audio interface for 32 kHz; the audio-clock frame
    /// pacing absorbs the fractional difference from the true DAC rate.
    /// </summary>
    public const int AudioSampleRate = 32_000;

    public N64Cartridge Cartridge { get; }

    public N64Memory Memory { get; }

    public Vr4300Cpu Cpu { get; }

    public int Width => Memory.VideoWidth;

    public int Height => Memory.VideoHeight;

    public double FramesPerSecond =>
        Cartridge.VideoRegion == N64VideoRegion.Ntsc ? NtscFramesPerSecond : 50.0;

    public ReadOnlySpan<uint> CurrentFrame => _frame.AsSpan(0, Width * Height);

    public long FrameNumber { get; private set; }

    public bool ReachedCartridgeEntryPoint { get; private set; }

    public long GraphicsTasksSubmitted { get; private set; }

    public long AudioTasksSubmitted { get; private set; }

    public N64RspTask? LastRspTask { get; private set; }

    public N64RspTask? LastGraphicsTask { get; private set; }

    public IN64GraphicsBackend GraphicsBackend => _graphicsBackend;

    public Fast3dRenderer Renderer => _renderer;

    public N64AudioProcessor AudioProcessor => _audioProcessor;

    public int BufferedAudioSampleCount => Memory.BufferedAudioSampleCount;

    public long DroppedAudioSampleCount => Memory.DroppedAudioSampleCount;

    public int ReadAudioSamples(Span<float> destination) => Memory.ReadAudioSamples(destination);

    public void ClearAudioSamples() => Memory.ClearAudioSamples();

    public static N64Machine Load(string path, string? savePath = null)
    {
        var cartridge = N64Cartridge.Load(path);
        return new N64Machine(cartridge, savePath);
    }

    public static N64Machine Create(N64Cartridge cartridge)
    {
        ArgumentNullException.ThrowIfNull(cartridge);
        return new N64Machine(cartridge, savePath: null);
    }

    public ReadOnlySpan<uint> RunFrame()
    {
        return RunFrame(renderGraphics: true, executeGraphicsTasks: true);
    }

    /// <summary>
    /// Advances a complete video field. A host that misses its real-time
    /// deadline may suppress raster work or skip that field's graphics task
    /// completely while CPU, audio, input, interrupts, and RSP completion
    /// continue at the cartridge cadence.
    /// </summary>
    public ReadOnlySpan<uint> RunFrame(
        bool renderGraphics,
        bool executeGraphicsTasks = true)
    {
        _graphicsBackend.RasterizationEnabled = renderGraphics;
        _executeGraphicsTasks = executeGraphicsTasks;
        try
        {
            RunInstructions(Memory.CpuTicksPerField);
            RenderVideoInterface();
            FrameNumber++;
            return CurrentFrame;
        }
        finally
        {
            _graphicsBackend.RasterizationEnabled = true;
            _executeGraphicsTasks = true;
        }
    }

    public void RunInstructions(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        for (var index = 0; index < count; index++)
        {
            Cpu.Step();
            ServiceRspTask();
            if (Cpu.ProgramCounter == Cartridge.EntryPoint && !ReachedCartridgeEntryPoint)
            {
                ReachedCartridgeEntryPoint = true;
                PatchBootMemorySize();
            }
        }
    }

    public void SetControllerState(int port, N64ControllerState state)
    {
        if (port is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        Memory.SetControllerState(port, state);
    }

    public void SetControllerConnected(int port, bool connected)
    {
        if (port is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        Memory.SetControllerConnected(port, connected);
    }

    public bool IsControllerConnected(int port)
    {
        if (port is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        return Memory.IsControllerConnected(port);
    }

    public N64ControllerState GetControllerState(int port)
    {
        if (port is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        return Memory.GetControllerState(port);
    }

    public byte[] SaveState()
    {
        using var payloadStream = new MemoryStream();
        using (var writer = new BinaryWriter(payloadStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(FrameNumber);
            writer.Write(ReachedCartridgeEntryPoint);
            writer.Write(GraphicsTasksSubmitted);
            writer.Write(AudioTasksSubmitted);
            Cpu.SaveState(writer);
            Memory.SaveState(writer);
            _audioProcessor.SaveState(writer);
            foreach (var pixel in _frame) writer.Write(pixel);
        }

        var payload = payloadStream.ToArray();
        var integrity = SHA256.HashData(payload);
        using var stateStream = new MemoryStream();
        using var stateWriter = new BinaryWriter(stateStream, Encoding.UTF8, leaveOpen: true);
        stateWriter.Write(StateMagic);
        stateWriter.Write(StateVersion);
        stateWriter.Write(_cartridgeIdentity);
        stateWriter.Write(payload.Length);
        stateWriter.Write(integrity);
        stateWriter.Write(payload);
        return stateStream.ToArray();
    }

    public void LoadState(ReadOnlySpan<byte> state)
    {
        using var stream = new MemoryStream(state.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(StateMagic.Length).AsSpan().SequenceEqual(StateMagic))
        {
            throw new InvalidDataException("This is not a Pixel64 save state.");
        }

        if (reader.ReadInt32() != StateVersion)
        {
            throw new InvalidDataException("This Pixel64 save-state version is not supported.");
        }

        if (!reader.ReadBytes(_cartridgeIdentity.Length).AsSpan().SequenceEqual(_cartridgeIdentity))
        {
            throw new InvalidDataException("This Pixel64 save state belongs to a different cartridge.");
        }

        var payloadLength = reader.ReadInt32();
        var expectedIntegrity = reader.ReadBytes(32);
        if (payloadLength < 0 || payloadLength > 24 * 1024 * 1024)
        {
            throw new InvalidDataException("The Pixel64 save-state payload length is invalid.");
        }

        var payload = reader.ReadBytes(payloadLength);
        if (payload.Length != payloadLength ||
            !CryptographicOperations.FixedTimeEquals(expectedIntegrity, SHA256.HashData(payload)) ||
            stream.Position != stream.Length)
        {
            throw new InvalidDataException("The Pixel64 save state is truncated or corrupt.");
        }

        using var payloadStream = new MemoryStream(payload, writable: false);
        using var payloadReader = new BinaryReader(payloadStream, Encoding.UTF8, leaveOpen: true);
        FrameNumber = payloadReader.ReadInt64();
        ReachedCartridgeEntryPoint = payloadReader.ReadBoolean();
        GraphicsTasksSubmitted = payloadReader.ReadInt64();
        AudioTasksSubmitted = payloadReader.ReadInt64();
        Cpu.LoadState(payloadReader);
        Memory.LoadState(payloadReader);
        _audioProcessor.LoadState(payloadReader);
        Memory.ClearAudioSamples();
        for (var index = 0; index < _frame.Length; index++) _frame[index] = payloadReader.ReadUInt32();
        if (payloadStream.Position != payloadStream.Length)
        {
            throw new InvalidDataException("The Pixel64 save-state payload has trailing data.");
        }
    }

    /// <summary>
    /// Writes the battery-backed store the cartridge declares. Only that
    /// store is written and only it is marked clean, so a dirty buffer for
    /// the other type can never be silently discarded.
    /// </summary>
    public void FlushBatterySave()
    {
        if (_savePath is null)
        {
            return;
        }

        var usesSram = Cartridge.SaveType is N64SaveType.Sram256Kbit or N64SaveType.FlashRam1Mbit;
        if (usesSram ? !Memory.SramDirty : !Memory.EepromDirty)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_savePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = _savePath + ".tmp";
        File.WriteAllBytes(temporaryPath, usesSram ? Memory.Sram : Memory.Eeprom);
        File.Move(temporaryPath, _savePath, overwrite: true);
        if (usesSram)
        {
            Memory.MarkSramFlushed();
        }
        else
        {
            Memory.MarkEepromFlushed();
        }
    }

    private void LoadBatterySave()
    {
        if (_savePath is null)
        {
            return;
        }

        var candidate = File.Exists(_savePath)
            ? _savePath
            : File.Exists(_savePath + ".tmp") ? _savePath + ".tmp" : null;
        if (candidate is null)
        {
            return;
        }

        // The cartridge declares its store, so the file is never identified by
        // length — two save types can legitimately share a size.
        var data = File.ReadAllBytes(candidate);
        if (Cartridge.SaveType is N64SaveType.Sram256Kbit or N64SaveType.FlashRam1Mbit)
        {
            Memory.LoadSram(data);
        }
        else
        {
            Memory.LoadEeprom(data);
        }

        if (!string.Equals(candidate, _savePath, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(candidate, _savePath, overwrite: true);
        }
    }

    private void RenderVideoInterface()
    {
        var format = Memory.ViControl & 3;
        // The VI width register is the frame-buffer stride; games can run
        // wider than our 320-pixel output (GoldenEye uses 440), in which
        // case we show the left 320 columns rather than shearing rows.
        var sourceStride = (int)Math.Max(Memory.ViWidth, 1);
        var sourceWidth = Math.Min(sourceStride, Width);
        var origin = Memory.ViOrigin & 0x7FFFFF;
        if (format is not (2u or 3u))
        {
            Array.Fill(_frame, 0xFF000000);
            return;
        }

        // The frame buffer always lives in RDRAM; convert it from one direct
        // span instead of resolving every pixel address through the bus.
        var sourceBytes = (long)Height * sourceStride * (format == 2 ? 2 : 4);
        if (origin + sourceBytes <= Memory.Rdram.Length)
        {
            RenderVideoInterfaceFromRdram(format, sourceStride, sourceWidth, origin);
            return;
        }

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (x >= sourceWidth)
                {
                    _frame[(y * Width) + x] = 0xFF000000;
                    continue;
                }

                var pixelIndex = (uint)((y * sourceStride) + x);
                _frame[(y * Width) + x] = format == 2
                    ? ConvertRgba5551(Memory.ReadUInt16(origin + (pixelIndex * 2)))
                    : ConvertRgba8888(Memory.ReadUInt32(origin + (pixelIndex * 4)));
            }
        }
    }

    private void RenderVideoInterfaceFromRdram(uint format, int sourceStride, int sourceWidth, uint origin)
    {
        var source = Memory.Rdram.AsSpan((int)origin);
        for (var y = 0; y < Height; y++)
        {
            var row = y * Width;
            var sourceRow = y * sourceStride;
            if (format == 2)
            {
                for (var x = 0; x < sourceWidth; x++)
                {
                    _frame[row + x] = ConvertRgba5551(
                        BinaryPrimitives.ReadUInt16BigEndian(source.Slice((sourceRow + x) * 2, 2)));
                }
            }
            else
            {
                for (var x = 0; x < sourceWidth; x++)
                {
                    _frame[row + x] = ConvertRgba8888(
                        BinaryPrimitives.ReadUInt32BigEndian(source.Slice((sourceRow + x) * 4, 4)));
                }
            }

            _frame.AsSpan(row + sourceWidth, Width - sourceWidth).Fill(0xFF000000);
        }
    }

    /// <summary>
    /// IPL3 sizes memory by probing RDRAM controller registers Pixel64 does
    /// not model, leaving osMemSize (0x80000318) at zero. Games such as
    /// GoldenEye trust that field and refuse to boot with no RAM, so publish
    /// the real 8 MiB once control transfers to the cartridge.
    /// </summary>
    private void PatchBootMemorySize()
    {
        if (Memory.ReadUInt32(0x80000318) == 0)
        {
            Memory.WriteUInt32(0x80000318, N64Memory.RdramSize);
        }
    }

    private void ServiceRspTask()
    {
        if (!Memory.TryBeginRspTask(out var task))
        {
            return;
        }

        LastRspTask = task;
        switch (task.Type)
        {
            case 1:
                GraphicsTasksSubmitted++;
                LastGraphicsTask = task;
                if (_executeGraphicsTasks)
                {
                    _graphicsBackend.Execute(task);
                }

                Memory.CompleteRspTask();
                Memory.CompleteDisplayProcessor();
                break;
            case 2:
                AudioTasksSubmitted++;
                _audioProcessor.Execute(task);
                Memory.CompleteRspTask();
                break;
            default:
                Memory.CompleteRspTask();
                break;
        }
    }

    private static uint ConvertRgba5551(ushort pixel)
    {
        var red = (uint)((pixel >> 11) & 31);
        var green = (uint)((pixel >> 6) & 31);
        var blue = (uint)((pixel >> 1) & 31);
        red = (red << 3) | (red >> 2);
        green = (green << 3) | (green >> 2);
        blue = (blue << 3) | (blue >> 2);
        return 0xFF000000 | (red << 16) | (green << 8) | blue;
    }

    private static uint ConvertRgba8888(uint pixel) =>
        0xFF000000 |
        ((pixel >> 8) & 0x00FF0000) |
        ((pixel >> 8) & 0x0000FF00) |
        ((pixel >> 8) & 0x000000FF);
}
