using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace PixelDeck.Emulation.N64;

/// <summary>
/// A portable, deterministic snapshot of one submitted N64 graphics task and
/// the RDRAM contents visible immediately before the backend executes it.
/// This is the replay boundary for renderer regression tests and future
/// backend comparisons. It does not serialize the full cartridge image, but
/// RDRAM can contain game-derived code and assets, so captures remain local
/// diagnostic evidence and must not be distributed without permission.
/// </summary>
public sealed class N64GraphicsTaskCapture
{
    private const int FormatVersion = 1;
    private const int MaximumCompressedBytes = N64Memory.RdramSize + (1024 * 1024);
    private static readonly byte[] Magic = "P64GFX01"u8.ToArray();
    private readonly byte[] _rdram;

    private N64GraphicsTaskCapture(N64RspTask task, byte[] rdram)
    {
        Task = task;
        _rdram = rdram;
        RdramSha256 = Convert.ToHexString(SHA256.HashData(rdram));
    }

    public N64RspTask Task { get; }

    public ReadOnlyMemory<byte> Rdram => _rdram;

    public string RdramSha256 { get; }

    public static N64GraphicsTaskCapture Create(
        N64RspTask task,
        ReadOnlySpan<byte> rdram)
    {
        if (task.Type != 1)
        {
            throw new ArgumentException("Only graphics RSP tasks can be captured.", nameof(task));
        }

        if (rdram.Length != N64Memory.RdramSize)
        {
            throw new ArgumentException(
                $"A graphics capture requires exactly {N64Memory.RdramSize:N0} RDRAM bytes.",
                nameof(rdram));
        }

        return new N64GraphicsTaskCapture(task, rdram.ToArray());
    }

    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(
            Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The capture path has no parent directory.", nameof(path)));
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

    public static N64GraphicsTaskCapture Load(string path)
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

        using var compressed = new MemoryStream();
        using (var compressor = new BrotliStream(
                   compressed,
                   CompressionLevel.Optimal,
                   leaveOpen: true))
        {
            compressor.Write(_rdram);
        }

        if (compressed.Length <= 0 || compressed.Length > MaximumCompressedBytes)
        {
            throw new InvalidDataException("The compressed graphics capture has an invalid size.");
        }

        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        WriteTask(writer, Task);
        writer.Write(_rdram.Length);
        writer.Write(checked((int)compressed.Length));
        writer.Write(SHA256.HashData(_rdram));
        compressed.Position = 0;
        compressed.CopyTo(destination);
    }

    public static N64GraphicsTaskCapture Read(Stream source)
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
                throw new InvalidDataException("The file is not a Pixel64 graphics capture.");
            }

            if (reader.ReadInt32() != FormatVersion)
            {
                throw new InvalidDataException("The Pixel64 graphics capture version is unsupported.");
            }

            var task = ReadTask(reader);
            if (task.Type != 1)
            {
                throw new InvalidDataException("The capture does not contain a graphics RSP task.");
            }

            var uncompressedLength = reader.ReadInt32();
            var compressedLength = reader.ReadInt32();
            if (uncompressedLength != N64Memory.RdramSize ||
                compressedLength <= 0 ||
                compressedLength > MaximumCompressedBytes)
            {
                throw new InvalidDataException("The graphics capture declares an invalid payload size.");
            }

            var expectedHash = ReadExactly(reader, SHA256.HashSizeInBytes);
            var compressed = ReadExactly(reader, compressedLength);
            if (source.CanSeek && source.Position != source.Length)
            {
                throw new InvalidDataException("The graphics capture contains trailing data.");
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
                        "The graphics capture expands beyond the declared RDRAM size.");
                }
            }

            var actualHash = SHA256.HashData(rdram);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                throw new InvalidDataException("The graphics capture checksum does not match.");
            }

            return new N64GraphicsTaskCapture(task, rdram);
        }
        catch (EndOfStreamException exception)
        {
            throw new InvalidDataException("The graphics capture is truncated.", exception);
        }
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

    private static void WriteTask(BinaryWriter writer, N64RspTask task)
    {
        writer.Write(task.Type);
        writer.Write(task.Flags);
        writer.Write(task.BootMicrocodePointer);
        writer.Write(task.BootMicrocodeSize);
        writer.Write(task.MicrocodePointer);
        writer.Write(task.MicrocodeSize);
        writer.Write(task.MicrocodeDataPointer);
        writer.Write(task.MicrocodeDataSize);
        writer.Write(task.DramStackPointer);
        writer.Write(task.DramStackSize);
        writer.Write(task.OutputBufferPointer);
        writer.Write(task.OutputBufferSizePointer);
        writer.Write(task.DataPointer);
        writer.Write(task.DataSize);
        writer.Write(task.YieldDataPointer);
        writer.Write(task.YieldDataSize);
    }

    private static N64RspTask ReadTask(BinaryReader reader) =>
        new(
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32(),
            reader.ReadUInt32());
}
