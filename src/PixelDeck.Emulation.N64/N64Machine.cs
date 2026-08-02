using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace PixelDeck.Emulation.N64;

public sealed class N64Machine
{
    private const int StateVersion = 9;
    private const int PreviousStateVersion = 8;
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
    private readonly string? _controllerPakPath;
    private readonly Fast3dRenderer _renderer;
    [SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "This is the intentional graphics-backend boundary for future conformant RDP implementations.")]
    private readonly IN64GraphicsBackend _graphicsBackend;
    [SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "This is the intentional audio-backend boundary for interchangeable RSP audio implementations.")]
    private readonly IN64AudioBackend _audioBackend;
    private readonly N64AudioProcessor _audioProcessor;
    private bool _captureNextGraphicsTask;
    private long _fieldExecutionTicks;
    private long _graphicsExecutionTicks;
    private long _audioExecutionTicks;
    private long _videoInterfaceTicks;

    private N64Machine(N64Cartridge cartridge, string? savePath)
    {
        Cartridge = cartridge;
        Memory = new N64Memory(cartridge);
        _renderer = new Fast3dRenderer(Memory);

        // Pixel64 renders through its own Fast3D pipeline. An earlier build
        // could mirror each task into a native paraLLEl-RDP bridge, but that
        // ran this renderer first and used its lowered command batch as input,
        // so it was a validation harness that cost a second pass rather than a
        // faster path. It was removed in favour of improving this renderer.
        _graphicsBackend = _renderer;
        GraphicsBackendStatus = _renderer.Name;
        _audioProcessor = new N64AudioProcessor(Memory);
        _audioBackend = _audioProcessor;
        Cpu = new Vr4300Cpu(Memory, cartridge.Cic, cartridge.VideoRegion);
        _cartridgeIdentity = SHA256.HashData(cartridge.Rom);
        _savePath = savePath;
        _controllerPakPath = savePath is null || !cartridge.UsesControllerPak
            ? null
            : cartridge.SaveType == N64SaveType.None
                ? savePath
                : Path.ChangeExtension(savePath, ".mpk");
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

    public bool IsVideoOutputActive =>
        (Memory.ViControl & 3) is 2u or 3u &&
        HasActiveVideoWindow(Memory.ViHorizontalVideo);

    public double FramesPerSecond =>
        Cartridge.VideoRegion == N64VideoRegion.Ntsc ? NtscFramesPerSecond : 50.0;

    public ReadOnlySpan<uint> CurrentFrame => _frame.AsSpan(0, Width * Height);

    public long FrameNumber { get; private set; }

    public bool ReachedCartridgeEntryPoint { get; private set; }

    public long GraphicsTasksSubmitted { get; private set; }

    public long AudioTasksSubmitted { get; private set; }

    /// <summary>
    /// Cumulative host-side timings used to identify which Pixel64 subsystem
    /// prevents real-time playback. These counters do not affect emulated time.
    /// </summary>
    public N64PerformanceSnapshot Performance => new(
        FrameNumber,
        GraphicsTasksSubmitted,
        AudioTasksSubmitted,
        Stopwatch.GetElapsedTime(0, _fieldExecutionTicks),
        Stopwatch.GetElapsedTime(0, _graphicsExecutionTicks),
        Stopwatch.GetElapsedTime(0, _audioExecutionTicks),
        Stopwatch.GetElapsedTime(0, _videoInterfaceTicks));

    public N64RspTask? LastRspTask { get; private set; }

    public N64RspTask? LastGraphicsTask { get; private set; }

    public N64RspTask? LastAudioTask { get; private set; }

    public IN64GraphicsBackend GraphicsBackend => _graphicsBackend;

    public string GraphicsBackendStatus { get; }

    public Fast3dRenderer Renderer => _renderer;

    /// <summary>
    /// Whether the game is driving the Rumble Pak motor in <paramref name="port"/>.
    /// </summary>
    public bool IsRumbleMotorActive(int port) => Memory.IsRumbleMotorActive(port);

    public N64GraphicsTaskCapture? LastGraphicsCapture { get; private set; }

    public N64AudioProcessor AudioProcessor => _audioProcessor;

    public IN64AudioBackend AudioBackend => _audioBackend;

    public int BufferedAudioSampleCount => Memory.BufferedAudioSampleCount;

    public long DroppedAudioSampleCount => Memory.DroppedAudioSampleCount;

    public int ReadAudioSamples(Span<float> destination) => Memory.ReadAudioSamples(destination);

    public void ClearAudioSamples() => Memory.ClearAudioSamples();

    /// <summary>
    /// Captures the next submitted graphics task and its pre-execution RDRAM
    /// image. Capture is explicitly one-shot because cloning 8 MiB every task
    /// would disrupt real-time emulation.
    /// </summary>
    public void RequestGraphicsTaskCapture()
    {
        LastGraphicsCapture = null;
        _captureNextGraphicsTask = true;
    }

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
        var fieldStarted = Stopwatch.GetTimestamp();
        try
        {
            RunInstructions(Memory.CpuTicksPerField);
            var videoStarted = Stopwatch.GetTimestamp();
            RenderVideoInterface();
            _videoInterfaceTicks += Stopwatch.GetTimestamp() - videoStarted;
            FrameNumber++;
            return CurrentFrame;
        }
        finally
        {
            _fieldExecutionTicks += Stopwatch.GetTimestamp() - fieldStarted;
        }
    }

    public void RunInstructions(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var remaining = count;
        while (remaining > 0)
        {
            // libultra's idle thread is an unconditional branch-to-self with a
            // NOP delay slot. Interpreting that pair millions of times per
            // second needlessly steals host time from the RDP. Advance the
            // emulated clocks in bulk, but stop at the first device or CP0
            // timer event so interrupt timing remains observable to software.
            var idleTicks = Cpu.TrySkipIdleLoop(remaining);
            if (idleTicks > 0)
            {
                remaining -= idleTicks;
                continue;
            }

            var executed = Cpu.RunCachedBlock(remaining);
            if (executed == 0)
            {
                Cpu.Step();
                executed = 1;
            }

            remaining -= executed;
            if (Memory.RspTaskPending)
            {
                ServiceRspTask();
            }
            if (Cpu.ProgramCounter == Cartridge.EffectiveEntryPoint &&
                !ReachedCartridgeEntryPoint)
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
            _audioBackend.SaveState(writer);
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

        var stateVersion = reader.ReadInt32();
        if (stateVersion is not (PreviousStateVersion or StateVersion))
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
        Memory.LoadState(payloadReader, stateVersion);
        _audioBackend.LoadState(payloadReader);
        Memory.ClearAudioSamples();
        for (var index = 0; index < _frame.Length; index++) _frame[index] = payloadReader.ReadUInt32();
        if (payloadStream.Position != payloadStream.Length)
        {
            throw new InvalidDataException("The Pixel64 save-state payload has trailing data.");
        }
    }

    /// <summary>
    /// Writes every persistent store installed for the cartridge. Cartridge
    /// save hardware and a Controller Pak are independent devices, so titles
    /// such as Mario Kart 64 can safely persist both in the same session.
    /// </summary>
    public void FlushBatterySave()
    {
        if (_savePath is not null && Cartridge.SaveType != N64SaveType.None)
        {
            var usesSram = Cartridge.SaveType is N64SaveType.Sram256Kbit or N64SaveType.FlashRam1Mbit;
            if (usesSram ? Memory.SramDirty : Memory.EepromDirty)
            {
                WriteSaveAtomically(_savePath, usesSram ? Memory.Sram : Memory.Eeprom);
                if (usesSram)
                {
                    Memory.MarkSramFlushed();
                }
                else
                {
                    Memory.MarkEepromFlushed();
                }
            }
        }

        if (_controllerPakPath is not null && Memory.ControllerPakDirty)
        {
            WriteSaveAtomically(_controllerPakPath, Memory.ControllerPak);
            Memory.MarkControllerPakFlushed();
        }
    }

    private void LoadBatterySave()
    {
        if (_savePath is not null && Cartridge.SaveType != N64SaveType.None)
        {
            var data = ReadSaveWithRecovery(_savePath);
            if (data is not null)
            {
                if (Cartridge.SaveType is N64SaveType.Sram256Kbit or N64SaveType.FlashRam1Mbit)
                {
                    Memory.LoadSram(data);
                }
                else
                {
                    Memory.LoadEeprom(data);
                }
            }
        }

        if (_controllerPakPath is not null)
        {
            var data = ReadSaveWithRecovery(_controllerPakPath);
            if (data is not null)
            {
                Memory.LoadControllerPak(data);
            }
        }
    }

    private static void WriteSaveAtomically(string path, byte[] data)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, data);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static byte[]? ReadSaveWithRecovery(string path)
    {
        var candidate = File.Exists(path)
            ? path
            : File.Exists(path + ".tmp") ? path + ".tmp" : null;
        if (candidate is null)
        {
            return null;
        }

        // The cartridge profile declares the store, so the file is never
        // identified by length; two save types can legitimately share a size.
        var data = File.ReadAllBytes(candidate);
        if (!string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(candidate, path, overwrite: true);
        }

        return data;
    }

    internal void RenderVideoInterface()
    {
        var format = Memory.ViControl & 3;
        // Rows are addressed by the frame-buffer stride but only the visible
        // width is presented, so a cartridge that allocates a wider buffer
        // than it displays (GoldenEye strides 440) reads without shearing.
        var sourceStride = (int)Math.Max(Memory.ViWidth, 1);
        var sourceWidth = Math.Min(sourceStride, Width);
        var origin = Memory.ViOrigin & 0x7FFFFF;
        if (!IsVideoOutputActive)
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

    /// <summary>
    /// A zero-width horizontal VI window is hardware blanking, not an
    /// instruction to keep scanning the last-sized framebuffer. Libultra's
    /// osViBlack implements transitions by writing H_START to zero while the
    /// game is free to recycle the hidden framebuffer memory.
    /// </summary>
    internal static bool HasActiveVideoWindow(uint horizontalVideo)
    {
        var start = (horizontalVideo >> 16) & 0x3FF;
        var end = horizontalVideo & 0x3FF;
        return end > start;
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
                if (_captureNextGraphicsTask)
                {
                    LastGraphicsCapture = N64GraphicsTaskCapture.Create(task, Memory.Rdram);
                    _captureNextGraphicsTask = false;
                }

                var graphicsStarted = Stopwatch.GetTimestamp();
                _graphicsBackend.Execute(task);
                _graphicsExecutionTicks += Stopwatch.GetTimestamp() - graphicsStarted;

                Memory.CompleteRspTask();
                Memory.CompleteDisplayProcessor();
                break;
            case 2:
                AudioTasksSubmitted++;
                LastAudioTask = task;
                var audioStarted = Stopwatch.GetTimestamp();
                _audioBackend.Execute(task);
                _audioExecutionTicks += Stopwatch.GetTimestamp() - audioStarted;
                Memory.CompleteRspTask();
                break;
            default:
                Memory.TryExecuteCic6105BootTask();
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
