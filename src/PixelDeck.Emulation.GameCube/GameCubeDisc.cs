using System.Buffers.Binary;
using System.Text;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Which television standard a disc was pressed for, resolved from the
/// country code in its game ID.
/// </summary>
public enum GameCubeRegion
{
    Unknown,
    NtscUsa,
    NtscJapan,
    Pal
}

/// <summary>
/// The 0x440-byte structure at the very start of a GameCube disc, known to the
/// SDK as <c>boot.bin</c>. Every offset a boot needs comes from here.
/// </summary>
public sealed record GameCubeDiscHeader(
    string GameCode,
    string MakerCode,
    byte DiscNumber,
    byte Version,
    bool AudioStreaming,
    byte StreamBufferSize,
    string Title,
    uint DebugMonitorOffset,
    uint DebugMonitorLoadAddress,
    uint MainExecutableOffset,
    uint FileSystemOffset,
    uint FileSystemSize,
    uint MaximumFileSystemSize,
    uint UserPosition,
    uint UserLength)
{
    /// <summary>The six-character ID players recognise, such as <c>GMSE01</c>.</summary>
    public string GameId => GameCode + MakerCode;

    public char CountryCode => GameCode.Length > 3 ? GameCode[3] : '\0';

    public GameCubeRegion Region => CountryCode switch
    {
        'E' or 'N' or 'Z' => GameCubeRegion.NtscUsa,
        'J' => GameCubeRegion.NtscJapan,
        'P' or 'D' or 'F' or 'S' or 'I' or 'H' or 'U' or 'X' or 'Y' => GameCubeRegion.Pal,
        _ => GameCubeRegion.Unknown
    };

    public string RegionText => Region switch
    {
        GameCubeRegion.NtscUsa => "NTSC-U",
        GameCubeRegion.NtscJapan => "NTSC-J",
        GameCubeRegion.Pal => "PAL",
        _ => "UNKNOWN"
    };

    /// <summary>
    /// PAL discs run at 50 Hz; everything else at the NTSC field rate. Both
    /// are the nominal figures, not the video clock a running console would
    /// derive.
    /// </summary>
    public double FramesPerSecond => Region == GameCubeRegion.Pal ? 50.0 : 59.94;
}

/// <summary>
/// What the library needs to know about a disc without booting it: enough to
/// name it, badge it, and say honestly what PixelCube can do with it.
/// </summary>
public sealed record GameCubeDiscSummary(
    string Title,
    string GameId,
    string RegionText,
    string ContainerName,
    bool IsReadable,
    bool IsPlayable,
    string CompatibilityMessage);

/// <summary>
/// A GameCube disc: its header, its file system, and its boot executable.
/// </summary>
/// <remarks>
/// This is deliberately the only part of PixelCube that is finished. A disc is
/// the one thing that can be parsed and checked completely before a single
/// instruction executes, and having it verified means that when the CPU does
/// arrive, a wrong entry point or a misread FST is already ruled out.
/// </remarks>
public sealed class GameCubeDisc : IDisposable
{
    /// <summary>Identifies a GameCube disc. Sits at offset 0x1C.</summary>
    public const uint MagicWord = 0xC233_9F3D;

    /// <summary>Identifies a Wii disc. Sits at offset 0x18.</summary>
    public const uint WiiMagicWord = 0x5D1C_9EA3;

    /// <summary>Where the apploader begins on every retail disc.</summary>
    public const long AppLoaderOffset = 0x2440;

    private const int HeaderSize = 0x440;
    private const int TitleOffset = 0x20;
    private const int TitleLength = 0x3E0;
    private const int MaximumFileSystemBytes = 8 * 1024 * 1024;

    private readonly GameCubeDiscImage _image;
    private readonly GameCubeTraceLog? _trace;
    private GameCubeFileSystem? _fileSystem;

    private GameCubeDisc(
        GameCubeDiscImage image,
        GameCubeDiscHeader header,
        GameCubeAppLoaderInfo appLoader,
        GameCubeTraceLog? trace)
    {
        _image = image;
        _trace = trace;
        Header = header;
        AppLoader = appLoader;
    }

    public GameCubeDiscHeader Header { get; }

    public GameCubeAppLoaderInfo AppLoader { get; }

    public string ContainerName => _image.ContainerName;

    public long Length => _image.Length;

    /// <summary>
    /// The disc's file table, parsed on first use because a library scan never
    /// needs it and it costs a read of up to a few megabytes.
    /// </summary>
    public GameCubeFileSystem FileSystem => _fileSystem ??= ReadFileSystem();

    /// <summary>
    /// Opens a disc and validates its header. The caller owns the result and
    /// must dispose it; the underlying image stays open for as long as it does.
    /// </summary>
    public static GameCubeDisc Open(string path, GameCubeTraceLog? trace = null)
    {
        var image = GameCubeDiscImage.Open(path, trace);
        try
        {
            return Open(image, trace);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    internal static GameCubeDisc Open(GameCubeDiscImage image, GameCubeTraceLog? trace = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        var header = new byte[HeaderSize];
        image.Read(0, header);

        if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x18, 4)) == WiiMagicWord)
        {
            throw new InvalidDataException(
                "This is a Wii disc. PixelCube emulates the GameCube only.");
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x1C, 4));
        if (magic != MagicWord)
        {
            throw new InvalidDataException(
                "This file does not contain a GameCube disc image: the 0xC2339F3D " +
                $"magic word is missing (found 0x{magic:X8}).");
        }

        var parsed = ParseHeader(header);
        var appLoaderBytes = new byte[GameCubeAppLoaderInfo.HeaderSize];
        image.Read(AppLoaderOffset, appLoaderBytes);
        var appLoader = GameCubeAppLoaderInfo.Parse(appLoaderBytes);

        trace?.Write(
            GameCubeTraceChannel.Disc,
            GameCubeTraceLevel.Information,
            $"disc header: id={parsed.GameId} region={parsed.RegionText} " +
            $"disc={parsed.DiscNumber} revision={parsed.Version} " +
            $"title=\"{parsed.Title}\"");
        trace?.Write(
            GameCubeTraceChannel.Disc,
            GameCubeTraceLevel.Debug,
            $"disc layout: dol=0x{parsed.MainExecutableOffset:X8} " +
            $"fst=0x{parsed.FileSystemOffset:X8}+0x{parsed.FileSystemSize:X} " +
            $"user=0x{parsed.UserPosition:X8}+0x{parsed.UserLength:X} " +
            $"streaming={parsed.AudioStreaming}");
        trace?.Write(
            GameCubeTraceChannel.Boot,
            GameCubeTraceLevel.Debug,
            $"apploader: date={appLoader.Date} entry=0x{appLoader.EntryPoint:X8} " +
            $"size={appLoader.Size} trailer={appLoader.TrailerSize}");

        return new GameCubeDisc(image, parsed, appLoader, trace);
    }

    /// <summary>
    /// Reads only what a library card needs, and never throws: an unreadable
    /// or unsupported disc still has to appear in the gallery with a straight
    /// answer about why it will not start.
    /// </summary>
    /// <remarks>
    /// Nothing is traced for a disc that reads correctly. A library scan opens
    /// every disc it finds, and a trace that reports each success turns the
    /// log into a directory listing; the failures are what is worth a line.
    /// </remarks>
    public static GameCubeDiscSummary Inspect(string path, GameCubeTraceLog? trace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using var disc = Open(path);
            return new GameCubeDiscSummary(
                disc.Header.Title,
                disc.Header.GameId,
                disc.Header.RegionText,
                disc.ContainerName,
                IsReadable: true,
                IsPlayable: false,
                $"PixelCube reads this disc ({disc.Header.GameId}, {disc.Header.RegionText}) and " +
                "loads its boot image, but has no execution core yet. Launching produces a trace " +
                "log rather than a game.");
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException
                      or InvalidDataException or NotSupportedException)
        {
            trace?.Write(
                GameCubeTraceChannel.Disc,
                GameCubeTraceLevel.Warning,
                $"disc \"{Path.GetFileName(path)}\" could not be read: {exception.Message}");
            return new GameCubeDiscSummary(
                Path.GetFileNameWithoutExtension(path),
                string.Empty,
                "UNKNOWN",
                Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
                IsReadable: false,
                IsPlayable: false,
                exception.Message);
        }
    }

    /// <summary>Reads <paramref name="length"/> bytes from the raw disc.</summary>
    public byte[] Read(long offset, int length)
    {
        var data = _image.Read(offset, length);
        _trace?.Write(
            GameCubeTraceChannel.Disc,
            GameCubeTraceLevel.Verbose,
            $"dvd read: offset=0x{offset:X8} length=0x{length:X}");
        return data;
    }

    /// <summary>
    /// Reads the boot executable named by the header — the <c>main.dol</c>
    /// the apploader would normally place in memory.
    /// </summary>
    public GameCubeExecutable ReadBootExecutable() =>
        GameCubeExecutable.Read(_image, Header.MainExecutableOffset, _trace);

    /// <summary>Reads a file listed in the disc's file system.</summary>
    public byte[] ReadFile(GameCubeFileSystemEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsDirectory)
        {
            throw new ArgumentException("Directories have no contents to read.", nameof(entry));
        }

        return Read(entry.Offset, checked((int)entry.Length));
    }

    public void Dispose() => _image.Dispose();

    private GameCubeFileSystem ReadFileSystem()
    {
        if (Header.FileSystemSize is 0 or > MaximumFileSystemBytes)
        {
            _trace?.Write(
                GameCubeTraceChannel.Disc,
                GameCubeTraceLevel.Warning,
                $"file system: refusing a declared size of {Header.FileSystemSize} bytes");
            return GameCubeFileSystem.Empty;
        }

        var fileSystem = GameCubeFileSystem.Parse(
            _image.Read(Header.FileSystemOffset, (int)Header.FileSystemSize));
        _trace?.Write(
            GameCubeTraceChannel.Disc,
            GameCubeTraceLevel.Information,
            $"file system: {fileSystem.Entries.Count} entries, " +
            $"{fileSystem.Files.Count} files");
        return fileSystem;
    }

    private static GameCubeDiscHeader ParseHeader(ReadOnlySpan<byte> header) => new(
        GameCode: DecodeAscii(header[..4]),
        MakerCode: DecodeAscii(header.Slice(4, 2)),
        DiscNumber: header[6],
        Version: header[7],
        AudioStreaming: header[8] != 0,
        StreamBufferSize: header[9],
        Title: DecodeAscii(header.Slice(TitleOffset, TitleLength)),
        DebugMonitorOffset: BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0x400, 4)),
        DebugMonitorLoadAddress: BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0x404, 4)),
        MainExecutableOffset: BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0x420, 4)),
        FileSystemOffset: BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0x424, 4)),
        FileSystemSize: BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0x428, 4)),
        MaximumFileSystemSize: BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0x42C, 4)),
        UserPosition: BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0x430, 4)),
        UserLength: BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0x434, 4)));

    /// <summary>
    /// Reads a fixed-width ASCII field, stopping at the first NUL and
    /// discarding anything unprintable. Disc titles are padding-filled and
    /// occasionally carry stray bytes.
    /// </summary>
    internal static string DecodeAscii(ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        var text = terminator < 0 ? bytes : bytes[..terminator];

        var builder = new StringBuilder(text.Length);
        foreach (var value in text)
        {
            if (value is >= 0x20 and <= 0x7E)
            {
                builder.Append((char)value);
            }
        }

        return builder.ToString().Trim();
    }
}

/// <summary>
/// The apploader header at offset 0x2440: the small program the IPL runs to
/// place the game's executable in memory. PixelCube does not run it yet, but
/// its build date is the most reliable indication of which SDK a disc was made
/// with, which is worth having in a trace.
/// </summary>
public sealed record GameCubeAppLoaderInfo(
    string Date,
    uint EntryPoint,
    int Size,
    int TrailerSize)
{
    public const int HeaderSize = 0x20;

    internal static GameCubeAppLoaderInfo Parse(ReadOnlySpan<byte> header) => new(
        GameCubeDisc.DecodeAscii(header[..0x10]),
        BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0x10, 4)),
        BinaryPrimitives.ReadInt32BigEndian(header.Slice(0x14, 4)),
        BinaryPrimitives.ReadInt32BigEndian(header.Slice(0x18, 4)));
}
