using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// A backend-neutral sequence of native RDP commands and the RDRAM visible
/// when command capture began. Current Fast3D capture marks a trace incomplete
/// when HLE geometry has not yet been lowered to native RDP triangle packets.
/// RDRAM can contain game-derived assets and must be treated as local
/// diagnostic evidence.
/// </summary>
public sealed class N64RdpTrace
{
    private const int FormatVersion = 2;
    private const int MaximumCommandCount = 1_000_000;
    private const int MaximumCompressedBytes = N64Memory.RdramSize + (1024 * 1024);
    private const int MaximumMicrocodeBytes = 256;
    private static readonly byte[] Magic = "P64RDP01"u8.ToArray();
    private readonly byte[] _rdram;
    private readonly N64RdpCommand[] _commands;

    internal N64RdpTrace(
        ReadOnlySpan<byte> rdram,
        IEnumerable<N64RdpCommand> commands,
        string microcode,
        int omittedHlePrimitiveCommands,
        long unsupportedSourceCommands)
    {
        if (rdram.Length != N64Memory.RdramSize)
        {
            throw new ArgumentException(
                $"RDP traces require exactly {N64Memory.RdramSize} bytes of RDRAM.",
                nameof(rdram));
        }

        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(microcode);
        ArgumentOutOfRangeException.ThrowIfNegative(omittedHlePrimitiveCommands);
        ArgumentOutOfRangeException.ThrowIfNegative(unsupportedSourceCommands);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            unsupportedSourceCommands,
            int.MaxValue);

        _rdram = rdram.ToArray();
        _commands = commands
            .Select(command => new N64RdpCommand(command.CopyWords()))
            .ToArray();
        if (_commands.Length > MaximumCommandCount)
        {
            throw new ArgumentOutOfRangeException(nameof(commands));
        }

        Microcode = microcode;
        OmittedHlePrimitiveCommands = omittedHlePrimitiveCommands;
        UnsupportedSourceCommands = (int)unsupportedSourceCommands;
        RdramSha256 = Convert.ToHexString(SHA256.HashData(_rdram));
        TraceSha256 = Convert.ToHexString(ComputeLogicalChecksum(
            _rdram,
            _commands,
            Microcode,
            OmittedHlePrimitiveCommands,
            UnsupportedSourceCommands));
    }

    public ReadOnlyMemory<byte> Rdram => _rdram;

    public IReadOnlyList<N64RdpCommand> Commands => _commands;

    public string Microcode { get; }

    public int OmittedHlePrimitiveCommands { get; }

    public int UnsupportedSourceCommands { get; }

    public bool IsComplete =>
        OmittedHlePrimitiveCommands == 0 &&
        UnsupportedSourceCommands == 0;

    public string RdramSha256 { get; }

    public string TraceSha256 { get; }

    public static N64RdpTrace Capture(N64GraphicsTaskCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var memory = N64GraphicsReplay.CreateReplayMemory();
        capture.Rdram.Span.CopyTo(memory.Rdram);
        var renderer = new Fast3dRenderer(memory);
        renderer.BeginRdpTraceCapture();
        renderer.Execute(capture.Task);
        return renderer.EndRdpTraceCapture(capture.Rdram.Span);
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.WriteThrough))
            {
                Write(stream);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static N64RdpTrace Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public void Write(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The destination stream is not writable.", nameof(destination));
        }

        var microcodeBytes = Encoding.UTF8.GetBytes(Microcode);
        if (microcodeBytes.Length > MaximumMicrocodeBytes)
        {
            throw new InvalidDataException("The RDP trace microcode name is too long.");
        }

        using var compressed = new MemoryStream();
        using (var compressor = new BrotliStream(
                   compressed,
                   CompressionLevel.Optimal,
                   leaveOpen: true))
        {
            compressor.Write(_rdram);
        }

        if (compressed.Length is <= 0 or > MaximumCompressedBytes)
        {
            throw new InvalidDataException("The compressed RDP trace has an invalid size.");
        }

        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(microcodeBytes.Length);
        writer.Write(OmittedHlePrimitiveCommands);
        writer.Write(UnsupportedSourceCommands);
        writer.Write(_commands.Length);
        writer.Write(_rdram.Length);
        writer.Write(checked((int)compressed.Length));
        writer.Write(Convert.FromHexString(TraceSha256));
        writer.Write(microcodeBytes);
        foreach (var command in _commands)
        {
            writer.Write((byte)command.Words.Length);
            foreach (var word in command.Words.Span)
            {
                writer.Write(word);
            }
        }

        compressed.Position = 0;
        compressed.CopyTo(destination);
    }

    public static N64RdpTrace Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The source stream is not readable.", nameof(source));
        }

        try
        {
            using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
            if (!ReadExactly(reader, Magic.Length).AsSpan().SequenceEqual(Magic))
            {
                throw new InvalidDataException("The file is not a Pixel64 RDP trace.");
            }

            if (reader.ReadInt32() != FormatVersion)
            {
                throw new InvalidDataException("The Pixel64 RDP trace version is unsupported.");
            }

            var microcodeLength = reader.ReadInt32();
            var omittedCommands = reader.ReadInt32();
            var unsupportedSourceCommands = reader.ReadInt32();
            var commandCount = reader.ReadInt32();
            var uncompressedLength = reader.ReadInt32();
            var compressedLength = reader.ReadInt32();
            if (microcodeLength is < 0 or > MaximumMicrocodeBytes ||
                omittedCommands < 0 ||
                unsupportedSourceCommands < 0 ||
                commandCount is < 0 or > MaximumCommandCount ||
                uncompressedLength != N64Memory.RdramSize ||
                compressedLength is <= 0 or > MaximumCompressedBytes)
            {
                throw new InvalidDataException("The RDP trace declares invalid dimensions.");
            }

            var expectedChecksum = ReadExactly(reader, SHA256.HashSizeInBytes);
            var microcode = Encoding.UTF8.GetString(ReadExactly(reader, microcodeLength));
            var commands = new N64RdpCommand[commandCount];
            for (var index = 0; index < commands.Length; index++)
            {
                var wordCount = reader.ReadByte();
                if (wordCount is < 2 or > N64RdpCommand.MaximumWordCount)
                {
                    throw new InvalidDataException("The RDP trace contains an invalid command length.");
                }

                var words = new uint[wordCount];
                for (var word = 0; word < words.Length; word++)
                {
                    words[word] = reader.ReadUInt32();
                }

                commands[index] = new N64RdpCommand(words);
            }

            var compressed = ReadExactly(reader, compressedLength);
            if (source.CanSeek && source.Position != source.Length)
            {
                throw new InvalidDataException("The RDP trace contains trailing data.");
            }

            var rdram = new byte[uncompressedLength];
            using (var compressedStream = new MemoryStream(compressed, writable: false))
            using (var decompressor = new BrotliStream(
                       compressedStream,
                       CompressionMode.Decompress,
                       leaveOpen: false))
            {
                decompressor.ReadExactly(rdram);
                if (decompressor.ReadByte() != -1)
                {
                    throw new InvalidDataException(
                        "The RDP trace expands beyond the declared RDRAM size.");
                }
            }

            var actualChecksum = ComputeLogicalChecksum(
                rdram,
                commands,
                microcode,
                omittedCommands,
                unsupportedSourceCommands);
            if (!CryptographicOperations.FixedTimeEquals(actualChecksum, expectedChecksum))
            {
                throw new InvalidDataException("The RDP trace checksum does not match.");
            }

            return new N64RdpTrace(
                rdram,
                commands,
                microcode,
                omittedCommands,
                unsupportedSourceCommands);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("The RDP trace is truncated.", exception);
        }
    }

    private static byte[] ComputeLogicalChecksum(
        ReadOnlySpan<byte> rdram,
        N64RdpCommand[] commands,
        string microcode,
        int omittedCommands,
        int unsupportedSourceCommands)
    {
        using var payload = new MemoryStream(rdram.Length + (commands.Length * 16));
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            var microcodeBytes = Encoding.UTF8.GetBytes(microcode);
            writer.Write(microcodeBytes.Length);
            writer.Write(microcodeBytes);
            writer.Write(omittedCommands);
            writer.Write(unsupportedSourceCommands);
            writer.Write(commands.Length);
            foreach (var command in commands)
            {
                writer.Write((byte)command.Words.Length);
                foreach (var word in command.Words.Span)
                {
                    writer.Write(word);
                }
            }

            writer.Write(rdram);
        }

        return SHA256.HashData(payload.GetBuffer().AsSpan(0, checked((int)payload.Length)));
    }

    private static byte[] ReadExactly(BinaryReader reader, int count)
    {
        var bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
        {
            throw new EndOfStreamException();
        }

        return bytes;
    }
}
