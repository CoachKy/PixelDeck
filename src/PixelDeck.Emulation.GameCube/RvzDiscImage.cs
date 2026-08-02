using System.Buffers.Binary;
using ZstdSharp;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// An RVZ disc image: Dolphin's compressed disc container.
/// </summary>
/// <remarks>
/// <para>
/// RVZ is a much richer container than CISO. A disc is cut into fixed chunks
/// ("groups"), each compressed independently, and the map that says which
/// chunks cover which part of the disc is itself compressed. On top of that,
/// RVZ omits the pseudorandom junk a real disc is padded with, storing a seed
/// where a run of junk used to be.
/// </para>
/// <para>
/// Only the GameCube disc type is handled. The Wii variant adds encrypted
/// partitions and per-block hashes, and PixelCube does not emulate a Wii.
/// </para>
/// <para>
/// Junk runs read back as zeroes rather than being regenerated. Reconstructing
/// them needs the lagged Fibonacci generator Nintendo pads discs with, and
/// nothing reads disc padding — but "nothing reads it" is an expectation, not
/// a fact, so every junk run is counted on the unimplemented channel. If a
/// game ever does read one, the tally will say so by name instead of the
/// wrong bytes quietly becoming somebody's afternoon.
/// </para>
/// </remarks>
internal sealed class RvzDiscImage : GameCubeDiscImage
{
    public const uint Signature = 0x5256_5A01; // "RVZ\x01"
    public const uint WiaSignature = 0x5749_4101; // "WIA\x01"

    private const int Header1Size = 0x48;
    private const int RawDataEntrySize = 24;
    private const int GroupEntrySize = 12;
    private const int MaximumChunkSize = 16 * 1024 * 1024;
    private const int MaximumEntries = 1 << 20;

    /// <summary>
    /// Set in a group's size field when the stored bytes are compressed.
    /// </summary>
    /// <remarks>
    /// Determined from the file rather than from memory, because getting it
    /// backwards fails silently: group 0 of Metroid Prime stores 0xB0B1 bytes,
    /// declares a packed size of 0x1B098 and expands to a 0x20000 chunk, which
    /// is only consistent if those stored bytes are compressed. A group with
    /// the bit clear has a stored size equal to its packed size.
    /// </remarks>
    private const uint GroupIsCompressed = 0x8000_0000;

    /// <summary>Set in a packed run's size when the run is junk, not data.</summary>
    private const uint PackedRunIsJunk = 0x8000_0000;

    /// <summary>
    /// The state a junk run carries in place of its bytes: seventeen 32-bit
    /// words, the seed of the lagged Fibonacci generator a disc is padded
    /// with.
    /// </summary>
    /// <remarks>
    /// Sixty-eight bytes, not four. Getting this wrong desynchronises the
    /// whole packed stream after the first junk run, so a group whose real
    /// data comes first decodes perfectly and one whose padding comes first
    /// decodes to nothing — which is exactly how it presented: a disc header
    /// that read correctly and a file table that read as empty.
    /// </remarks>
    private const int PackedJunkSeedSize = 17 * sizeof(uint);

    /// <summary>Group data offsets are stored in units of four bytes.</summary>
    private const int GroupOffsetUnit = 4;

    /// <summary>
    /// How much of the disc RVZ keeps verbatim in its own header rather than
    /// in a compressed group.
    /// </summary>
    /// <remarks>
    /// This is not an optimisation to ignore: the raw data entries genuinely
    /// begin at 0x80, so a reader that does not serve this range from the
    /// header returns zeroes for the disc's magic word and game ID, and the
    /// image looks corrupt rather than merely unhandled.
    /// </remarks>
    private const int EmbeddedHeaderSize = 0x80;

    /// <summary>
    /// The boundary a raw data entry's start is rounded down to before its
    /// groups are tiled across it.
    /// </summary>
    /// <remarks>
    /// This is why the first entry of a GameCube RVZ reports an offset of 0x80
    /// while its first group still begins at disc offset zero: the 0x80 bytes
    /// held in the header are counted as part of the group even though they
    /// are not stored in it. Reading the entry's offset literally shifts every
    /// group by 0x80 and produces a disc whose header is right and whose every
    /// other field is nonsense.
    /// </remarks>
    private const int GroupAlignment = 0x8000;

    private enum RvzCompression
    {
        None = 0,
        Purge = 1,
        Bzip2 = 2,
        Lzma = 3,
        Lzma2 = 4,
        Zstandard = 5
    }

    private readonly FileStream _stream;
    private readonly GameCubeTraceLog? _trace;
    private readonly RawDataEntry[] _rawData;
    private readonly GroupEntry[] _groups;
    private readonly RvzCompression _compression;
    private readonly int _chunkSize;

    /// <summary>
    /// The most recently expanded group. A sequential read walks a 128 KB
    /// chunk a few bytes at a time, and expanding it once per access instead
    /// of once per chunk makes reading a disc header take seconds.
    /// </summary>
    private readonly byte[] _discHeader = new byte[EmbeddedHeaderSize];
    private byte[] _cachedGroup = [];
    private long _cachedGroupIndex = -1;

    public RvzDiscImage(FileStream stream, GameCubeTraceLog? trace)
    {
        _stream = stream;
        _trace = trace;

        var header1 = new byte[Header1Size];
        ReadFileExactly(stream, header1, 0);

        var magic = BinaryPrimitives.ReadUInt32BigEndian(header1);
        if (magic == WiaSignature)
        {
            throw new NotSupportedException(
                "WIA disc images are not readable yet. RVZ, the format Dolphin writes today, " +
                "is supported; convert or re-dump to RVZ, ISO or CISO.");
        }

        if (magic != Signature)
        {
            throw new InvalidDataException("The file does not contain an RVZ disc image.");
        }

        var header2Size = BinaryPrimitives.ReadUInt32BigEndian(header1.AsSpan(0x0C, 4));
        Length = (long)BinaryPrimitives.ReadUInt64BigEndian(header1.AsSpan(0x24, 8));
        if (header2Size is < 0xDC or > 0x10000 || Length <= 0)
        {
            throw new InvalidDataException("The RVZ image declares an unusable header.");
        }

        var header2 = new byte[header2Size];
        ReadFileExactly(stream, header2, Header1Size);

        var discType = BinaryPrimitives.ReadUInt32BigEndian(header2.AsSpan(0x00, 4));
        _compression = (RvzCompression)BinaryPrimitives.ReadUInt32BigEndian(header2.AsSpan(0x04, 4));
        _chunkSize = (int)BinaryPrimitives.ReadUInt32BigEndian(header2.AsSpan(0x0C, 4));

        if (discType != 1)
        {
            throw new NotSupportedException(discType == 2
                ? "This RVZ image contains a Wii disc. PixelCube emulates the GameCube only."
                : "This RVZ image does not contain a GameCube disc.");
        }

        if (_compression is not (RvzCompression.Zstandard or RvzCompression.None))
        {
            throw new NotSupportedException(
                $"This RVZ image uses {_compression} compression, which PixelCube cannot read. " +
                "Zstandard, the format Dolphin writes by default, is supported.");
        }

        if (_chunkSize is <= 0 or > MaximumChunkSize)
        {
            throw new InvalidDataException(
                $"The RVZ image declares an unusable chunk size of {_chunkSize} bytes.");
        }

        _rawData = ReadRawDataEntries(header2);
        _groups = ReadGroupEntries(header2);

        trace?.Write(
            GameCubeTraceChannel.Disc,
            GameCubeTraceLevel.Debug,
            $"rvz: compression={_compression} chunk={_chunkSize} " +
            $"rawData={_rawData.Length} groups={_groups.Length}");

        foreach (var entry in _rawData)
        {
            trace?.Write(
                GameCubeTraceChannel.Disc,
                GameCubeTraceLevel.Debug,
                $"rvz raw data: offset=0x{entry.DataOffset:X} size=0x{entry.DataSize:X} " +
                $"group={entry.GroupIndex} count={entry.GroupCount}");
        }

        header2.AsSpan(0x10, EmbeddedHeaderSize).CopyTo(_discHeader);
    }

    public override long Length { get; }

    public override string ContainerName => "RVZ";

    /// <summary>
    /// The GameCube disc header, which RVZ keeps uncompressed in its own
    /// header. Present so a library scan can name a disc without expanding
    /// anything.
    /// </summary>
    public static bool TryReadEmbeddedDiscHeader(FileStream stream, Span<byte> destination)
    {
        if (destination.Length < 0x80 || stream.Length < Header1Size + 0xD8)
        {
            return false;
        }

        var header2 = new byte[0xD8];
        ReadFileExactly(stream, header2, Header1Size);
        header2.AsSpan(0x10, 0x80).CopyTo(destination);
        return true;
    }

    public override void Read(long offset, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        // The head of the disc lives in the RVZ header, not in a group.
        if (offset < EmbeddedHeaderSize)
        {
            var fromHeader = (int)Math.Min(destination.Length, EmbeddedHeaderSize - offset);
            _discHeader.AsSpan((int)offset, fromHeader).CopyTo(destination);
            destination = destination[fromHeader..];
            offset += fromHeader;
        }

        while (!destination.IsEmpty)
        {
            if (!TryFindRawData(offset, out var entry))
            {
                // Not covered by any entry: unused disc area, which never held
                // anything a game reads.
                destination.Clear();
                return;
            }

            var alignedStart = (long)entry.DataOffset - ((long)entry.DataOffset % GroupAlignment);
            var offsetInEntry = offset - alignedStart;
            var groupIndex = entry.GroupIndex + (offsetInEntry / _chunkSize);
            var offsetInGroup = (int)(offsetInEntry % _chunkSize);

            var group = ExpandGroup(groupIndex);
            var available = Math.Max(0, group.Length - offsetInGroup);
            var wanted = Math.Min(destination.Length, _chunkSize - offsetInGroup);
            var copied = Math.Min(available, wanted);

            if (copied > 0)
            {
                group.AsSpan(offsetInGroup, copied).CopyTo(destination);
            }

            // A group that expanded short leaves the rest of the chunk as the
            // zeroes a blank disc region would read as.
            destination[copied..wanted].Clear();
            destination = destination[wanted..];
            offset += wanted;
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

    private bool TryFindRawData(long offset, out RawDataEntry entry)
    {
        foreach (var candidate in _rawData)
        {
            if ((ulong)offset >= candidate.DataOffset &&
                (ulong)offset < candidate.DataOffset + candidate.DataSize)
            {
                entry = candidate;
                return true;
            }
        }

        entry = default;
        return false;
    }

    private byte[] ExpandGroup(long groupIndex)
    {
        if (groupIndex == _cachedGroupIndex)
        {
            return _cachedGroup;
        }

        if (groupIndex < 0 || groupIndex >= _groups.Length)
        {
            return [];
        }

        var group = _groups[groupIndex];
        var storedSize = (int)(group.DataSize & ~GroupIsCompressed);
        if (storedSize == 0)
        {
            // An empty group is a chunk of zeroes that was not worth storing.
            _cachedGroup = [];
            _cachedGroupIndex = groupIndex;
            return _cachedGroup;
        }

        var stored = new byte[storedSize];
        ReadFileExactly(_stream, stored, (long)group.DataOffset * GroupOffsetUnit);

        var isPacked = group.PackedSize > 0;
        var expandedSize = isPacked ? (int)group.PackedSize : _chunkSize;
        var expanded = (group.DataSize & GroupIsCompressed) != 0
            ? Decompress(stored, expandedSize)
            : stored;

        _cachedGroup = isPacked ? Unpack(expanded) : expanded;
        _cachedGroupIndex = groupIndex;
        return _cachedGroup;
    }

    private byte[] Decompress(byte[] stored, int expandedSize)
    {
        if (_compression == RvzCompression.None)
        {
            return stored;
        }

        try
        {
            using var decompressor = new Decompressor();
            return decompressor.Unwrap(stored, expandedSize).ToArray();
        }
        catch (ZstdException exception)
        {
            _trace?.WriteOnce(
                GameCubeTraceChannel.Disc,
                GameCubeTraceLevel.Error,
                "rvz/decompress-failed",
                $"an RVZ group could not be decompressed: {exception.Message}");
            return [];
        }
    }

    /// <summary>
    /// Expands an RVZ-packed group: a sequence of runs, each either literal
    /// bytes or a seeded run of the junk a pressed disc is padded with.
    /// </summary>
    private byte[] Unpack(byte[] packed)
    {
        var output = new byte[_chunkSize];
        var written = 0;
        var position = 0;

        while (position + 4 <= packed.Length && written < output.Length)
        {
            var size = BinaryPrimitives.ReadUInt32BigEndian(packed.AsSpan(position, 4));
            position += 4;

            var isJunk = (size & PackedRunIsJunk) != 0;
            var runLength = (int)Math.Min(size & ~PackedRunIsJunk, (uint)(output.Length - written));

            if (isJunk)
            {
                // The generator seed follows, and then nothing: the run itself
                // was never stored. Left as zeroes, and counted.
                position += PackedJunkSeedSize;
                output.AsSpan(written, runLength).Clear();
                written += runLength;

                _trace?.WriteOnce(
                    GameCubeTraceChannel.Unimplemented,
                    GameCubeTraceLevel.Information,
                    "rvz/junk-data",
                    "RVZ junk padding is read as zeroes; PixelCube does not regenerate the " +
                    "disc's pseudorandom fill. No game is expected to read it.");
                continue;
            }

            if (runLength == 0)
            {
                continue;
            }

            var available = Math.Min(runLength, packed.Length - position);
            if (available <= 0)
            {
                // The stream ended mid-run. Everything already written stands;
                // the remainder stays zeroed.
                break;
            }

            packed.AsSpan(position, available).CopyTo(output.AsSpan(written, available));
            position += available;
            written += available;
        }

        return output;
    }

    private RawDataEntry[] ReadRawDataEntries(byte[] header2)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(header2.AsSpan(0xB4, 4));
        var offset = (long)BinaryPrimitives.ReadUInt64BigEndian(header2.AsSpan(0xB8, 8));
        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(header2.AsSpan(0xC0, 4));
        var table = ReadCompressedTable(offset, size, count, RawDataEntrySize, "raw data");

        var entries = new RawDataEntry[count];
        for (var index = 0; index < entries.Length; index++)
        {
            var record = table.AsSpan(index * RawDataEntrySize, RawDataEntrySize);
            entries[index] = new RawDataEntry(
                BinaryPrimitives.ReadUInt64BigEndian(record[..8]),
                BinaryPrimitives.ReadUInt64BigEndian(record.Slice(8, 8)),
                BinaryPrimitives.ReadUInt32BigEndian(record.Slice(16, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(record.Slice(20, 4)));
        }

        return entries;
    }

    private GroupEntry[] ReadGroupEntries(byte[] header2)
    {
        var count = BinaryPrimitives.ReadUInt32BigEndian(header2.AsSpan(0xC4, 4));
        var offset = (long)BinaryPrimitives.ReadUInt64BigEndian(header2.AsSpan(0xC8, 8));
        var size = (int)BinaryPrimitives.ReadUInt32BigEndian(header2.AsSpan(0xD0, 4));
        var table = ReadCompressedTable(offset, size, count, GroupEntrySize, "group");

        var entries = new GroupEntry[count];
        for (var index = 0; index < entries.Length; index++)
        {
            var record = table.AsSpan(index * GroupEntrySize, GroupEntrySize);
            entries[index] = new GroupEntry(
                BinaryPrimitives.ReadUInt32BigEndian(record[..4]),
                BinaryPrimitives.ReadUInt32BigEndian(record.Slice(4, 4)),
                BinaryPrimitives.ReadUInt32BigEndian(record.Slice(8, 4)));
        }

        return entries;
    }

    /// <summary>
    /// Reads one of the two tables that describe the file. Both are stored
    /// with the same compression as the disc data itself, which is why they
    /// cannot simply be read at a fixed offset.
    /// </summary>
    private byte[] ReadCompressedTable(
        long offset,
        int storedSize,
        uint count,
        int entrySize,
        string description)
    {
        if (count > MaximumEntries || storedSize <= 0 || offset <= 0)
        {
            throw new InvalidDataException(
                $"The RVZ image declares an unusable {description} table.");
        }

        var expectedSize = checked((int)count * entrySize);
        var stored = new byte[storedSize];
        ReadFileExactly(_stream, stored, offset);

        if (_compression == RvzCompression.None)
        {
            return stored.Length >= expectedSize
                ? stored
                : throw new InvalidDataException($"The RVZ {description} table is truncated.");
        }

        using var decompressor = new Decompressor();
        var expanded = decompressor.Unwrap(stored, expectedSize).ToArray();
        return expanded.Length >= expectedSize
            ? expanded
            : throw new InvalidDataException($"The RVZ {description} table is truncated.");
    }

    /// <summary>A span of the disc, and the groups that hold it.</summary>
    private readonly record struct RawDataEntry(
        ulong DataOffset,
        ulong DataSize,
        uint GroupIndex,
        uint GroupCount);

    /// <summary>
    /// One compressed chunk. <paramref name="DataSize"/> carries a flag in its
    /// top bit meaning the chunk was stored without compression, and
    /// <paramref name="PackedSize"/> is non-zero when the chunk is RVZ-packed.
    /// </summary>
    private readonly record struct GroupEntry(uint DataOffset, uint DataSize, uint PackedSize);
}
