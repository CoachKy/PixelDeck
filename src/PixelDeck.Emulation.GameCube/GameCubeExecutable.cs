using System.Buffers.Binary;

namespace PixelDeck.Emulation.GameCube;

/// <summary>One loadable region of a DOL executable.</summary>
public sealed record GameCubeExecutableSection(
    int Index,
    bool IsText,
    uint FileOffset,
    uint LoadAddress,
    uint Size,
    ReadOnlyMemory<byte> Data)
{
    public string Name => IsText ? $"text{Index}" : $"data{Index}";

    public uint EndAddress => LoadAddress + Size;
}

/// <summary>
/// A GameCube DOL executable — the format <c>main.dol</c> uses.
/// </summary>
/// <remarks>
/// The header is a fixed table of seven text and eleven data sections, each
/// with a file offset, a load address, and a size, followed by the BSS range
/// and the entry point. Unused slots are all zeroes.
///
/// PixelCube parses this now, ahead of the CPU that will run it, because the
/// section map is the first thing a Gekko interpreter needs and the first
/// thing worth checking against a known-good dump: if a disc's sections and
/// entry point match what Dolphin reports for the same disc, then everything
/// before the first instruction is already correct.
/// </remarks>
public sealed class GameCubeExecutable
{
    public const int TextSectionCount = 7;
    public const int DataSectionCount = 11;
    public const int HeaderSize = 0x100;

    /// <summary>
    /// A ceiling on a single section. Real ones are a few megabytes; anything
    /// past this is a misparse, and allocating on a bad length is how a
    /// malformed disc turns into an out-of-memory crash.
    /// </summary>
    private const uint MaximumSectionSize = 32 * 1024 * 1024;

    private GameCubeExecutable(
        IReadOnlyList<GameCubeExecutableSection> sections,
        uint bssAddress,
        uint bssSize,
        uint entryPoint)
    {
        Sections = sections;
        BssAddress = bssAddress;
        BssSize = bssSize;
        EntryPoint = entryPoint;
    }

    /// <summary>Every populated section, text first, in header order.</summary>
    public IReadOnlyList<GameCubeExecutableSection> Sections { get; }

    public uint BssAddress { get; }

    public uint BssSize { get; }

    /// <summary>The address the console jumps to once loading is complete.</summary>
    public uint EntryPoint { get; }

    public uint TotalSectionBytes => (uint)Sections.Sum(section => (long)section.Size);

    /// <summary>Reads a DOL that begins at <paramref name="offset"/> on a disc.</summary>
    public static GameCubeExecutable Read(
        GameCubeDiscImage image,
        long offset,
        GameCubeTraceLog? trace = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var header = image.Read(offset, HeaderSize);
        var sections = new List<GameCubeExecutableSection>(TextSectionCount + DataSectionCount);

        for (var index = 0; index < TextSectionCount + DataSectionCount; index++)
        {
            var isText = index < TextSectionCount;
            var slot = isText ? index : index - TextSectionCount;
            var tableIndex = index;

            var fileOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(tableIndex * 4, 4));
            var loadAddress = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x48 + (tableIndex * 4), 4));
            var size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x90 + (tableIndex * 4), 4));

            if (size == 0 || fileOffset == 0)
            {
                continue;
            }

            if (size > MaximumSectionSize)
            {
                trace?.Write(
                    GameCubeTraceChannel.Executable,
                    GameCubeTraceLevel.Warning,
                    $"dol section {(isText ? "text" : "data")}{slot} declares " +
                    $"{size:N0} bytes and was skipped");
                continue;
            }

            var data = image.Read(offset + fileOffset, (int)size);
            sections.Add(new GameCubeExecutableSection(
                slot,
                isText,
                fileOffset,
                loadAddress,
                size,
                data));
        }

        var bssAddress = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0xD8, 4));
        var bssSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0xDC, 4));
        var entryPoint = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0xE0, 4));

        var executable = new GameCubeExecutable(sections, bssAddress, bssSize, entryPoint);

        trace?.Write(
            GameCubeTraceChannel.Executable,
            GameCubeTraceLevel.Information,
            $"dol: {sections.Count} sections, {executable.TotalSectionBytes:N0} bytes, " +
            $"bss=0x{bssAddress:X8}+0x{bssSize:X} entry=0x{entryPoint:X8}");
        foreach (var section in sections)
        {
            trace?.Write(
                GameCubeTraceChannel.Executable,
                GameCubeTraceLevel.Debug,
                $"dol section {section.Name,-6} file=0x{section.FileOffset:X6} " +
                $"load=0x{section.LoadAddress:X8}-0x{section.EndAddress:X8} " +
                $"size=0x{section.Size:X}");
        }

        return executable;
    }
}
