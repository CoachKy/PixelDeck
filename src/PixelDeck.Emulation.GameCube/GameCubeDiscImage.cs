using System.Buffers.Binary;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Random access to the bytes of a GameCube disc, whatever container they
/// arrived in.
/// </summary>
/// <remarks>
/// Everything above this class addresses a disc the way the DVD hardware does:
/// a flat, seekable 1.36 GB image. Containers exist only because nobody stores
/// discs that way, so the container is resolved once, here, and never leaks
/// upward. Images are opened rather than read into memory — a GameCube disc is
/// larger than the console's entire address space and a library scan touches
/// only the first kilobyte of each one.
/// </remarks>
public abstract class GameCubeDiscImage : IDisposable
{
    /// <summary>The uncompressed size of the disc this image represents.</summary>
    public abstract long Length { get; }

    /// <summary>How the image was stored, for trace and compatibility text.</summary>
    public abstract string ContainerName { get; }

    /// <summary>
    /// Opens a disc image, choosing the container by content and falling back
    /// to the file extension. Formats PixelCube cannot read yet are refused by
    /// name, because "unsupported container" and "corrupt disc" need to be
    /// different answers.
    /// </summary>
    public static GameCubeDiscImage Open(string path, GameCubeTraceLog? trace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 0,
            FileOptions.RandomAccess);
        try
        {
            Span<byte> magic = stackalloc byte[4];
            var read = stream.Length >= 4 ? stream.Read(magic) : 0;
            var signature = read == 4 ? BinaryPrimitives.ReadUInt32BigEndian(magic) : 0u;

            GameCubeDiscImage image = signature switch
            {
                CisoDiscImage.Signature => new CisoDiscImage(stream),
                RvzDiscImage.Signature or RvzDiscImage.WiaSignature =>
                    new RvzDiscImage(stream, trace),
                GczSignature => throw new NotSupportedException(
                    "GCZ disc images are not readable yet. Convert the disc to RVZ, ISO or CISO."),
                _ => new RawDiscImage(stream)
            };

            trace?.Write(
                GameCubeTraceChannel.Disc,
                GameCubeTraceLevel.Information,
                $"disc image: container={image.ContainerName} " +
                $"stored={stream.Length:N0} bytes expanded={image.Length:N0} bytes");
            return image;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Fills <paramref name="destination"/> from <paramref name="offset"/>.
    /// Reads past the end of the disc yield zeroes rather than throwing,
    /// matching a real drive returning nothing for unwritten sectors.
    /// </summary>
    public abstract void Read(long offset, Span<byte> destination);

    public byte[] Read(long offset, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var buffer = new byte[length];
        Read(offset, buffer);
        return buffer;
    }

    public uint ReadUInt32BigEndian(long offset)
    {
        Span<byte> word = stackalloc byte[4];
        Read(offset, word);
        return BinaryPrimitives.ReadUInt32BigEndian(word);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    /// <summary>
    /// Fills <paramref name="destination"/> from the file, zeroing whatever a
    /// short or truncated file could not supply. A disc image that stops early
    /// is a damaged image, and a game reading past the damage should see the
    /// same nothing a drive would report rather than an exception from inside
    /// a memory access.
    /// </summary>
    private protected static void ReadFileExactly(
        FileStream stream,
        Span<byte> destination,
        long offset)
    {
        while (!destination.IsEmpty)
        {
            var read = RandomAccess.Read(stream.SafeFileHandle, destination, offset);
            if (read <= 0)
            {
                destination.Clear();
                return;
            }

            destination = destination[read..];
            offset += read;
        }
    }

    private const uint GczSignature = 0x01C0_0BB1;

    /// <summary>A plain 1:1 image: <c>.iso</c> and <c>.gcm</c>.</summary>
    private sealed class RawDiscImage : GameCubeDiscImage
    {
        private readonly FileStream _stream;

        public RawDiscImage(FileStream stream) => _stream = stream;

        public override long Length => _stream.Length;

        public override string ContainerName => "ISO";

        public override void Read(long offset, Span<byte> destination)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            if (destination.IsEmpty)
            {
                return;
            }

            if (offset >= _stream.Length)
            {
                destination.Clear();
                return;
            }

            var available = (int)Math.Min(destination.Length, _stream.Length - offset);
            ReadFileExactly(_stream, destination[..available], offset);
            destination[available..].Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A CISO image: a fixed-size block map in which absent blocks were all
    /// zeroes on the original disc and were simply not stored. GameCube discs
    /// are mostly padding, which is why this format halves them.
    /// </summary>
    /// <remarks>
    /// Layout: <c>"CISO"</c>, a little-endian block size, and then one byte per
    /// block filling the rest of a 0x8000-byte header — 1 for a stored block,
    /// 0 for a block of zeroes. Stored blocks follow the header back to back in
    /// index order, so their file positions are a running total rather than a
    /// table, and that total is computed once at open.
    /// </remarks>
    private sealed class CisoDiscImage : GameCubeDiscImage
    {
        public const uint Signature = 0x4349_534F; // "CISO"

        private const int HeaderSize = 0x8000;
        private const int MapOffset = 8;
        private const int MapEntryCount = HeaderSize - MapOffset;
        private const int MinimumBlockSize = 2 * 1024;
        private const int MaximumBlockSize = 16 * 1024 * 1024;

        private readonly FileStream _stream;
        private readonly long[] _blockPositions;
        private readonly int _blockSize;

        public CisoDiscImage(FileStream stream)
        {
            _stream = stream;

            var header = new byte[HeaderSize];
            if (stream.Length < HeaderSize)
            {
                throw new InvalidDataException("The CISO image is missing its block map.");
            }

            ReadFileExactly(stream, header, 0);
            _blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            if (_blockSize is < MinimumBlockSize or > MaximumBlockSize ||
                (_blockSize & (_blockSize - 1)) != 0)
            {
                throw new InvalidDataException(
                    $"The CISO image declares an unusable block size of {_blockSize} bytes.");
            }

            _blockPositions = new long[MapEntryCount];
            var position = (long)HeaderSize;
            var storedBlocks = 0;
            var lastUsedBlock = -1;
            for (var index = 0; index < MapEntryCount; index++)
            {
                if (header[MapOffset + index] == 0)
                {
                    _blockPositions[index] = -1;
                    continue;
                }

                _blockPositions[index] = position;
                position += _blockSize;
                storedBlocks++;
                lastUsedBlock = index;
            }

            // The expanded length runs to the end of the last stored block:
            // trailing absent blocks are padding no game reads, and counting
            // the full 32760-block span would report every CISO as the same
            // size. Writers commonly truncate the final block to the end of
            // the source image, so a file shorter than the block count implies
            // subtract that shortfall rather than round up to a whole block.
            var expectedFileLength = HeaderSize + ((long)storedBlocks * _blockSize);
            var shortfall = Math.Max(0, expectedFileLength - stream.Length);
            Length = lastUsedBlock < 0
                ? 0
                : ((long)(lastUsedBlock + 1) * _blockSize) - shortfall;
            StoredBlockCount = storedBlocks;
        }

        public override long Length { get; }

        public int StoredBlockCount { get; }

        public override string ContainerName => "CISO";

        public override void Read(long offset, Span<byte> destination)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(offset);

            while (!destination.IsEmpty)
            {
                var block = offset / _blockSize;
                var withinBlock = (int)(offset % _blockSize);
                var count = Math.Min(destination.Length, _blockSize - withinBlock);

                if (block >= _blockPositions.Length || _blockPositions[block] < 0)
                {
                    // An absent block was zeroes on the original disc.
                    destination[..count].Clear();
                }
                else
                {
                    ReadFileExactly(
                        _stream,
                        destination[..count],
                        _blockPositions[block] + withinBlock);
                }

                destination = destination[count..];
                offset += count;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
