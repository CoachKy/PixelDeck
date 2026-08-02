using System.Buffers.Binary;
using System.Text;

namespace PixelDeck.App.Tests;

/// <summary>
/// Builds small but structurally genuine GameCube disc images.
/// </summary>
/// <remarks>
/// The one disc in the local library is 1.4 GB, which is the wrong thing to
/// hang a test suite on: it cannot be committed, it is slow to read, and a
/// test that skips when it is absent proves nothing on a clean checkout. These
/// images are a few tens of kilobytes and exercise the same parsers, so the
/// real disc is left for the end-to-end check it is actually good for.
/// </remarks>
internal static class GameCubeTestSupport
{
    public const int BlockSize = 0x8000;
    public const int ImageSize = 3 * BlockSize;

    public const uint MagicWord = 0xC233_9F3D;
    public const string GameCode = "GTSE";
    public const string MakerCode = "01";
    public const string Title = "PixelCube Test Disc";

    public const uint ExecutableOffset = 0x3000;
    public const uint FileSystemOffset = 0x4000;

    public const uint EntryPoint = 0x8000_3100;
    public const uint TextLoadAddress = 0x8000_3100;
    public const uint DataLoadAddress = 0x8010_0000;
    public const uint BssAddress = 0x8020_0000;
    public const uint BssSize = 0x1000;
    public const int TextSize = 32;
    public const int DataSize = 16;

    /// <summary>Where the two payload files live, in the third block.</summary>
    public const uint InnerFileOffset = 0x1_0000;
    public const uint RootFileOffset = 0x1_0100;
    public const int InnerFileLength = 16;
    public const int RootFileLength = 8;

    /// <summary>
    /// A plain 1:1 disc image with a header, an apploader stub, a two-section
    /// DOL, and a file table holding one directory and two files.
    /// </summary>
    public static byte[] CreateDiscImage()
    {
        var image = new byte[ImageSize];

        Encoding.ASCII.GetBytes(GameCode).CopyTo(image, 0);
        Encoding.ASCII.GetBytes(MakerCode).CopyTo(image, 4);
        image[6] = 0;    // disc number
        image[7] = 2;    // revision
        image[8] = 1;    // audio streaming
        image[9] = 10;   // stream buffer size
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x1C, 4), MagicWord);
        Encoding.ASCII.GetBytes(Title).CopyTo(image, 0x20);

        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x400, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x404, 4), 0x8130_0000);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x420, 4), ExecutableOffset);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x424, 4), FileSystemOffset);

        var fileSystem = CreateFileSystemTable();
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x428, 4), (uint)fileSystem.Length);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x42C, 4), (uint)fileSystem.Length);

        WriteAppLoader(image);
        WriteExecutable(image);
        fileSystem.CopyTo(image.AsSpan((int)FileSystemOffset));

        // Payload bytes in the third block, so a CISO of this image has to
        // skip an absent block to reach them.
        for (var index = 0; index < InnerFileLength; index++)
        {
            image[InnerFileOffset + index] = (byte)(0xA0 + index);
        }

        for (var index = 0; index < RootFileLength; index++)
        {
            image[RootFileOffset + index] = (byte)(0x50 + index);
        }

        return image;
    }

    /// <summary>
    /// Wraps a raw image in a CISO container, storing only the blocks that
    /// contain something. The second block of <see cref="CreateDiscImage"/> is
    /// entirely zeroes and is therefore left out, which is the behaviour the
    /// reader has to reverse.
    /// </summary>
    public static byte[] CreateCompressedImage(byte[] rawImage)
    {
        const int headerSize = 0x8000;
        const int mapOffset = 8;

        var blockCount = rawImage.Length / BlockSize;
        var stored = new List<int>(blockCount);
        for (var block = 0; block < blockCount; block++)
        {
            if (rawImage.AsSpan(block * BlockSize, BlockSize).ContainsAnyExcept((byte)0))
            {
                stored.Add(block);
            }
        }

        var image = new byte[headerSize + (stored.Count * BlockSize)];
        Encoding.ASCII.GetBytes("CISO").CopyTo(image, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4, 4), BlockSize);
        foreach (var block in stored)
        {
            image[mapOffset + block] = 1;
        }

        for (var index = 0; index < stored.Count; index++)
        {
            rawImage.AsSpan(stored[index] * BlockSize, BlockSize)
                .CopyTo(image.AsSpan(headerSize + (index * BlockSize)));
        }

        return image;
    }

    /// <summary>The number of blocks a CISO of the test image actually stores.</summary>
    public static int ExpectedStoredBlockCount => 2;

    /// <summary>Where the synthetic RVZ places a junk run instead of data.</summary>
    public const uint RvzJunkStart = 0x1_0000;

    public const int RvzJunkLength = 0x100;

    /// <summary>
    /// Wraps a raw image in an RVZ container: Zstandard-compressed groups, the
    /// first 0x80 bytes held in the header rather than in a group, and a junk
    /// run in the last group.
    /// </summary>
    /// <remarks>
    /// The shape here is chosen to catch the two mistakes that are silent in
    /// this format. The header carries the disc's first 0x80 bytes while the
    /// raw data entry still claims to start at 0x80, so a reader that trusts
    /// that offset literally shifts every group. And real data follows a junk
    /// run, so a reader that mis-sizes the junk seed decodes the groups before
    /// it perfectly and everything after it as nothing.
    /// </remarks>
    public static byte[] CreateRvzImage(byte[] rawImage)
    {
        const int chunkSize = BlockSize;
        var groupCount = rawImage.Length / chunkSize;

        var groups = new List<(byte[] Stored, int PackedSize, bool Compressed)>(groupCount);
        for (var index = 0; index < groupCount; index++)
        {
            var chunk = rawImage.AsSpan(index * chunkSize, chunkSize);
            var packed = index * chunkSize == RvzJunkStart
                ? PackWithLeadingJunk(chunk)
                : PackAsLiteral(chunk);

            // One group is left uncompressed so both paths are exercised.
            var compressed = index != 1;
            var stored = compressed ? Compress(packed) : packed;
            groups.Add((stored, packed.Length, compressed));
        }

        var rawEntries = new byte[RawDataEntrySizeBytes];
        BinaryPrimitives.WriteUInt64BigEndian(rawEntries.AsSpan(0, 8), 0x80);
        BinaryPrimitives.WriteUInt64BigEndian(
            rawEntries.AsSpan(8, 8),
            (ulong)(rawImage.Length - 0x80));
        BinaryPrimitives.WriteUInt32BigEndian(rawEntries.AsSpan(16, 4), 0);
        BinaryPrimitives.WriteUInt32BigEndian(rawEntries.AsSpan(20, 4), (uint)groupCount);
        var rawEntriesStored = Compress(rawEntries);

        var groupEntries = new byte[groupCount * GroupEntrySizeBytes];
        var groupEntriesStored = Array.Empty<byte>();

        // Two passes: the group entries hold file offsets, which depend on the
        // size of the compressed tables that precede the group data.
        for (var pass = 0; pass < 2; pass++)
        {
            var dataStart = AlignUp(
                HeaderSizeBytes + rawEntriesStored.Length + groupEntriesStored.Length,
                4);
            var position = dataStart;
            for (var index = 0; index < groupCount; index++)
            {
                var entry = groupEntries.AsSpan(index * GroupEntrySizeBytes, GroupEntrySizeBytes);
                BinaryPrimitives.WriteUInt32BigEndian(entry[..4], (uint)(position / 4));
                BinaryPrimitives.WriteUInt32BigEndian(
                    entry.Slice(4, 4),
                    (uint)groups[index].Stored.Length |
                        (groups[index].Compressed ? 0x8000_0000u : 0));
                BinaryPrimitives.WriteUInt32BigEndian(
                    entry.Slice(8, 4),
                    (uint)groups[index].PackedSize);
                position = AlignUp(position + groups[index].Stored.Length, 4);
            }

            groupEntriesStored = Compress(groupEntries);
        }

        return Assemble(rawImage, groups, rawEntriesStored, groupEntriesStored);
    }

    private const int HeaderSizeBytes = 0x48 + 0xDC;
    private const int RawDataEntrySizeBytes = 24;
    private const int GroupEntrySizeBytes = 12;

    /// <summary>Seventeen 32-bit words of generator state.</summary>
    private const int JunkSeedSize = 17 * sizeof(uint);

    private static byte[] Assemble(
        byte[] rawImage,
        List<(byte[] Stored, int PackedSize, bool Compressed)> groups,
        byte[] rawEntriesStored,
        byte[] groupEntriesStored)
    {
        var rawEntriesOffset = HeaderSizeBytes;
        var groupEntriesOffset = rawEntriesOffset + rawEntriesStored.Length;
        var dataStart = AlignUp(groupEntriesOffset + groupEntriesStored.Length, 4);

        var total = dataStart;
        foreach (var group in groups)
        {
            total = AlignUp(total + group.Stored.Length, 4);
        }

        var image = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0, 4), 0x5256_5A01); // "RVZ\x01"
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x0C, 4), 0xDC);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(0x24, 8), (ulong)rawImage.Length);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(0x2C, 8), (ulong)total);

        var header2 = image.AsSpan(0x48);
        BinaryPrimitives.WriteUInt32BigEndian(header2[..4], 1);              // GameCube
        BinaryPrimitives.WriteUInt32BigEndian(header2.Slice(0x04, 4), 5);    // Zstandard
        BinaryPrimitives.WriteUInt32BigEndian(header2.Slice(0x0C, 4), BlockSize);
        rawImage.AsSpan(0, 0x80).CopyTo(header2.Slice(0x10, 0x80));
        BinaryPrimitives.WriteUInt32BigEndian(header2.Slice(0xB4, 4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(header2.Slice(0xB8, 8), (ulong)rawEntriesOffset);
        BinaryPrimitives.WriteUInt32BigEndian(header2.Slice(0xC0, 4), (uint)rawEntriesStored.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header2.Slice(0xC4, 4), (uint)groups.Count);
        BinaryPrimitives.WriteUInt64BigEndian(header2.Slice(0xC8, 8), (ulong)groupEntriesOffset);
        BinaryPrimitives.WriteUInt32BigEndian(header2.Slice(0xD0, 4), (uint)groupEntriesStored.Length);

        rawEntriesStored.CopyTo(image.AsSpan(rawEntriesOffset));
        groupEntriesStored.CopyTo(image.AsSpan(groupEntriesOffset));

        var position = dataStart;
        foreach (var group in groups)
        {
            group.Stored.CopyTo(image.AsSpan(position));
            position = AlignUp(position + group.Stored.Length, 4);
        }

        return image;
    }

    private static byte[] PackAsLiteral(ReadOnlySpan<byte> chunk)
    {
        var packed = new byte[4 + chunk.Length];
        BinaryPrimitives.WriteUInt32BigEndian(packed.AsSpan(0, 4), (uint)chunk.Length);
        chunk.CopyTo(packed.AsSpan(4));
        return packed;
    }

    private static byte[] PackWithLeadingJunk(ReadOnlySpan<byte> chunk)
    {
        var literal = chunk.Length - RvzJunkLength;
        var packed = new byte[4 + JunkSeedSize + 4 + literal];

        BinaryPrimitives.WriteUInt32BigEndian(
            packed.AsSpan(0, 4),
            0x8000_0000u | RvzJunkLength);
        // The seed itself is never read back; only its length matters here.
        BinaryPrimitives.WriteUInt32BigEndian(
            packed.AsSpan(4 + JunkSeedSize, 4),
            (uint)literal);
        chunk[RvzJunkLength..].CopyTo(packed.AsSpan(4 + JunkSeedSize + 4));
        return packed;
    }

    private static byte[] Compress(byte[] data)
    {
        using var compressor = new ZstdSharp.Compressor();
        return compressor.Wrap(data).ToArray();
    }

    private static int AlignUp(int value, int alignment) =>
        (value + alignment - 1) / alignment * alignment;

    private static void WriteAppLoader(byte[] image)
    {
        const int appLoaderOffset = 0x2440;
        Encoding.ASCII.GetBytes("2003/01/01").CopyTo(image, appLoaderOffset);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(appLoaderOffset + 0x10, 4), 0x8130_0000);
        BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(appLoaderOffset + 0x14, 4), 4096);
        BinaryPrimitives.WriteInt32BigEndian(image.AsSpan(appLoaderOffset + 0x18, 4), 256);
    }

    private static void WriteExecutable(byte[] image)
    {
        const uint textFileOffset = 0x100;
        const uint dataFileOffset = 0x120;
        var dol = image.AsSpan((int)ExecutableOffset);

        // Text section 0.
        BinaryPrimitives.WriteUInt32BigEndian(dol[..4], textFileOffset);
        BinaryPrimitives.WriteUInt32BigEndian(dol.Slice(0x48, 4), TextLoadAddress);
        BinaryPrimitives.WriteUInt32BigEndian(dol.Slice(0x90, 4), TextSize);

        // Data section 0 sits at table index 7, immediately after the seven
        // text slots.
        BinaryPrimitives.WriteUInt32BigEndian(dol.Slice(7 * 4, 4), dataFileOffset);
        BinaryPrimitives.WriteUInt32BigEndian(dol.Slice(0x48 + (7 * 4), 4), DataLoadAddress);
        BinaryPrimitives.WriteUInt32BigEndian(dol.Slice(0x90 + (7 * 4), 4), DataSize);

        BinaryPrimitives.WriteUInt32BigEndian(dol.Slice(0xD8, 4), BssAddress);
        BinaryPrimitives.WriteUInt32BigEndian(dol.Slice(0xDC, 4), BssSize);
        BinaryPrimitives.WriteUInt32BigEndian(dol.Slice(0xE0, 4), EntryPoint);

        for (var index = 0; index < TextSize; index++)
        {
            dol[(int)textFileOffset + index] = (byte)(0x10 + index);
        }

        for (var index = 0; index < DataSize; index++)
        {
            dol[(int)dataFileOffset + index] = (byte)(0xE0 + index);
        }
    }

    /// <summary>
    /// A four-entry file table: the root, a directory holding one file, and a
    /// second file at the root.
    /// </summary>
    private static byte[] CreateFileSystemTable()
    {
        const int entryCount = 4;
        var names = Encoding.ASCII.GetBytes("sub\0inner.bin\0root.bin\0");
        var table = new byte[(entryCount * 12) + names.Length];

        WriteEntry(table, 0, isDirectory: true, nameOffset: 0, offset: 0, length: entryCount);
        WriteEntry(table, 1, isDirectory: true, nameOffset: 0, offset: 0, length: 3);
        WriteEntry(table, 2, isDirectory: false, nameOffset: 4, offset: InnerFileOffset, length: InnerFileLength);
        WriteEntry(table, 3, isDirectory: false, nameOffset: 14, offset: RootFileOffset, length: RootFileLength);

        names.CopyTo(table, entryCount * 12);
        return table;
    }

    private static void WriteEntry(
        byte[] table,
        int index,
        bool isDirectory,
        uint nameOffset,
        uint offset,
        uint length)
    {
        var entry = table.AsSpan(index * 12, 12);
        entry[0] = (byte)(isDirectory ? 1 : 0);
        entry[1] = (byte)(nameOffset >> 16);
        entry[2] = (byte)(nameOffset >> 8);
        entry[3] = (byte)nameOffset;
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(4, 4), offset);
        BinaryPrimitives.WriteUInt32BigEndian(entry.Slice(8, 4), length);
    }
}
