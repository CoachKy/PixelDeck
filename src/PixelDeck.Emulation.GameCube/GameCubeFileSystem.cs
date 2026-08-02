using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace PixelDeck.Emulation.GameCube;

/// <summary>One entry in a disc's file system table.</summary>
/// <param name="Index">Position in the table, as the FST addresses it.</param>
/// <param name="Name">The entry's own name, without any parent path.</param>
/// <param name="Path">Full path from the root, using forward slashes.</param>
/// <param name="IsDirectory">Whether this entry holds other entries.</param>
/// <param name="Offset">
/// A file's position on the disc, or a directory's parent index.
/// </param>
/// <param name="Length">
/// A file's size in bytes, or the index one past a directory's last child.
/// </param>
public sealed record GameCubeFileSystemEntry(
    int Index,
    string Name,
    string Path,
    bool IsDirectory,
    uint Offset,
    uint Length);

/// <summary>
/// A GameCube disc's file table (the "FST").
/// </summary>
/// <remarks>
/// The format is twelve bytes per entry: a type byte, a 24-bit offset into a
/// string table that follows the entries, and two words whose meaning depends
/// on the type. Entry zero is the root, and its length field is the total
/// entry count — which is how the table's own size is discovered before any of
/// it has been read.
///
/// Parsing is total and forgiving: a malformed table yields the entries that
/// could be read rather than an exception, because a disc whose FST is
/// slightly wrong is far more useful to inspect than to reject.
/// </remarks>
public sealed class GameCubeFileSystem
{
    private const int EntrySize = 12;

    private readonly Dictionary<string, GameCubeFileSystemEntry> _byPath;

    private GameCubeFileSystem(
        IReadOnlyList<GameCubeFileSystemEntry> entries,
        IReadOnlyList<GameCubeFileSystemEntry> files)
    {
        Entries = entries;
        Files = files;
        _byPath = files.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
    }

    public static GameCubeFileSystem Empty { get; } = new([], []);

    /// <summary>Every entry, files and directories alike, in table order.</summary>
    public IReadOnlyList<GameCubeFileSystemEntry> Entries { get; }

    /// <summary>Only the files, in table order.</summary>
    public IReadOnlyList<GameCubeFileSystemEntry> Files { get; }

    public bool TryGetFile(
        string path,
        [NotNullWhen(true)] out GameCubeFileSystemEntry? entry) =>
        _byPath.TryGetValue(path, out entry);

    public static GameCubeFileSystem Parse(ReadOnlySpan<byte> table)
    {
        if (table.Length < EntrySize)
        {
            return Empty;
        }

        var declaredCount = BinaryPrimitives.ReadUInt32BigEndian(table.Slice(8, 4));
        var available = table.Length / EntrySize;
        var count = (int)Math.Min(declaredCount, (uint)available);
        if (count <= 0)
        {
            return Empty;
        }

        var stringTable = table[(count * EntrySize)..];
        var entries = new List<GameCubeFileSystemEntry>(count);
        var files = new List<GameCubeFileSystemEntry>(count);

        // A directory's length is the index one past its last descendant, so
        // walking entries in order while popping finished directories rebuilds
        // the paths without recursion.
        var openDirectories = new Stack<(string Path, uint End)>();
        openDirectories.Push((string.Empty, (uint)count));

        for (var index = 0; index < count; index++)
        {
            var record = table.Slice(index * EntrySize, EntrySize);
            var isDirectory = record[0] != 0;
            var nameOffset = (uint)((record[1] << 16) | (record[2] << 8) | record[3]);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(4, 4));
            var length = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(8, 4));

            while (openDirectories.Count > 1 && index >= openDirectories.Peek().End)
            {
                openDirectories.Pop();
            }

            var name = index == 0
                ? string.Empty
                : ReadName(stringTable, nameOffset);
            var parentPath = openDirectories.Peek().Path;
            var path = index == 0
                ? string.Empty
                : parentPath.Length == 0 ? name : $"{parentPath}/{name}";

            var entry = new GameCubeFileSystemEntry(
                index,
                name,
                path,
                isDirectory,
                offset,
                length);
            entries.Add(entry);
            if (isDirectory)
            {
                if (index > 0)
                {
                    openDirectories.Push((path, length));
                }
            }
            else
            {
                files.Add(entry);
            }
        }

        return new GameCubeFileSystem(entries, files);
    }

    private static string ReadName(ReadOnlySpan<byte> stringTable, uint offset)
    {
        if (offset >= (uint)stringTable.Length)
        {
            return string.Empty;
        }

        return GameCubeDisc.DecodeAscii(stringTable[(int)offset..]);
    }
}
