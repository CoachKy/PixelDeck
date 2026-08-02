using System.Globalization;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// The subsystem a trace record came from.
/// </summary>
/// <remarks>
/// A GameCube produces far more observable events per second than any console
/// PixelDeck already emulates, so "log everything" is not a usable setting:
/// the interesting line is always buried. Channels exist so a session can be
/// narrowed to the one subsystem under investigation before the run starts,
/// and so the cost of the channels nobody asked for is a single bit test.
/// </remarks>
[Flags]
public enum GameCubeTraceChannel : uint
{
    None = 0,

    /// <summary>Startup order: image opened, IPL replacement, entry point taken.</summary>
    Boot = 1u << 0,

    /// <summary>Disc image containers, header parsing, and DVD reads.</summary>
    Disc = 1u << 1,

    /// <summary>DOL/ELF section layout and load addresses.</summary>
    Executable = 1u << 2,

    /// <summary>Main memory and ARAM accesses, including unmapped ones.</summary>
    Memory = 1u << 3,

    /// <summary>Hardware register reads and writes in the 0xCC00_0000 block.</summary>
    Registers = 1u << 4,

    /// <summary>Gekko execution: branches, supervisor instructions, and stalls.</summary>
    Cpu = 1u << 5,

    /// <summary>Exceptions and interrupt delivery.</summary>
    Interrupts = 1u << 6,

    /// <summary>The DSP and its microcode.</summary>
    Dsp = 1u << 7,

    /// <summary>Flipper command processing and the GX pipeline.</summary>
    Graphics = 1u << 8,

    /// <summary>The video interface: mode, framebuffer origin, field timing.</summary>
    Video = 1u << 9,

    /// <summary>The audio interface and streaming.</summary>
    Audio = 1u << 10,

    /// <summary>Controller polling through SI.</summary>
    Input = 1u << 11,

    /// <summary>Memory card and save file activity.</summary>
    Storage = 1u << 12,

    /// <summary>
    /// Anything PixelCube was asked to do and cannot do yet. This is the
    /// channel that turns a black screen into a work list, so it is on by
    /// default.
    /// </summary>
    Unimplemented = 1u << 13,

    /// <summary>Host-side timing samples used to find what is too slow.</summary>
    Performance = 1u << 14,

    All =
        Boot | Disc | Executable | Memory | Registers | Cpu | Interrupts | Dsp |
        Graphics | Video | Audio | Input | Storage | Unimplemented | Performance,

    /// <summary>
    /// What a normal play session records: the decisions made once at startup,
    /// the things that went wrong, and the things that are still missing.
    /// Excludes the per-instruction channels, but keeps
    /// <see cref="Registers"/> — its per-access detail is at Debug and stays
    /// filtered out, while its counters are what reveal a register that is
    /// modelled and still wrong.
    /// </summary>
    Default =
        Boot | Disc | Executable | Interrupts | Storage | Unimplemented |
        Performance | Registers
}

/// <summary>
/// How serious a trace record is. Ordered so a threshold comparison works:
/// a log set to <see cref="Warning"/> also passes <see cref="Error"/>.
/// </summary>
public enum GameCubeTraceLevel
{
    Off = 0,
    Error = 1,
    Warning = 2,
    Information = 3,
    Debug = 4,
    Verbose = 5
}

/// <summary>
/// One trace line, with the context needed to place it in the run: which
/// emulated frame it belongs to and how long after startup it happened.
/// </summary>
public readonly record struct GameCubeTraceRecord(
    long Sequence,
    long Frame,
    TimeSpan Elapsed,
    GameCubeTraceChannel Channel,
    GameCubeTraceLevel Level,
    string Message)
{
    /// <summary>
    /// The single-line form written to files and forwarded to the dashboard's
    /// diagnostics log. Fixed-width leading fields so a long capture stays
    /// readable in a column-aligned editor.
    /// </summary>
    public string Format() => string.Create(
        CultureInfo.InvariantCulture,
        $"[{Elapsed.TotalSeconds,10:F4}] f{Frame,-7} {ShortLevel(Level)} {Channel,-13} {Message}");

    public override string ToString() => Format();

    private static string ShortLevel(GameCubeTraceLevel level) => level switch
    {
        GameCubeTraceLevel.Error => "ERR ",
        GameCubeTraceLevel.Warning => "WARN",
        GameCubeTraceLevel.Information => "INFO",
        GameCubeTraceLevel.Debug => "DBG ",
        GameCubeTraceLevel.Verbose => "VERB",
        _ => "OFF "
    };
}

/// <summary>How often a repeated trace key has been seen.</summary>
public readonly record struct GameCubeTraceCounter(string Key, long Count);

/// <summary>
/// What a trace log should record, and where. Resolved from the environment so
/// a channel can be opened for one run without rebuilding PixelDeck, which is
/// the only practical way to trace something that only reproduces on a
/// player's machine.
/// </summary>
/// <param name="Level">The most detailed level that will be recorded.</param>
/// <param name="Channels">The subsystems allowed to record.</param>
/// <param name="FilePath">Where to write, or null to keep the log in memory.</param>
/// <param name="ConfigurationWarning">
/// Set when a configuration string could not be understood. Carried rather
/// than thrown, because a bad diagnostic setting must not stop a game from
/// starting — but silently falling back would leave someone convinced that
/// tracing is on when it is not, so the log reports it on its first line.
/// </param>
public sealed record GameCubeTraceSettings(
    GameCubeTraceLevel Level,
    GameCubeTraceChannel Channels,
    string? FilePath = null,
    string? ConfigurationWarning = null)
{
    /// <summary>Names the environment variable that selects level and channels.</summary>
    public const string SpecificationVariable = "PIXELCUBE_TRACE";

    /// <summary>Names the environment variable that redirects the trace file.</summary>
    public const string FileVariable = "PIXELCUBE_TRACE_FILE";

    public static GameCubeTraceSettings Default { get; } =
        new(GameCubeTraceLevel.Information, GameCubeTraceChannel.Default);

    public static GameCubeTraceSettings Disabled { get; } =
        new(GameCubeTraceLevel.Off, GameCubeTraceChannel.None);

    /// <summary>
    /// The trace file every session writes unless redirected. It sits beside
    /// the dashboard's own <c>emulator.log</c> so both can be collected
    /// together from a bug report.
    /// </summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixelDeck",
        "pixelcube-trace.log");

    /// <summary>
    /// Reads <c>PIXELCUBE_TRACE</c> and <c>PIXELCUBE_TRACE_FILE</c>, falling
    /// back to <see cref="Default"/> and <see cref="DefaultFilePath"/>.
    /// </summary>
    public static GameCubeTraceSettings FromEnvironment() => FromEnvironment(
        Environment.GetEnvironmentVariable(SpecificationVariable),
        Environment.GetEnvironmentVariable(FileVariable));

    internal static GameCubeTraceSettings FromEnvironment(
        string? specification,
        string? filePath)
    {
        var resolvedPath = string.IsNullOrWhiteSpace(filePath)
            ? DefaultFilePath
            : Path.GetFullPath(filePath);

        if (string.IsNullOrWhiteSpace(specification))
        {
            return Default with { FilePath = resolvedPath };
        }

        if (TryParse(specification, out var parsed))
        {
            return parsed with { FilePath = resolvedPath };
        }

        return Default with
        {
            FilePath = resolvedPath,
            ConfigurationWarning =
                $"{SpecificationVariable}=\"{specification}\" could not be understood; " +
                "using the default level and channels. Expected \"<level>\" or " +
                "\"<level>:<channel>[,<channel>...]\"."
        };
    }

    /// <summary>
    /// Parses a specification such as <c>debug:disc,cpu</c>, <c>verbose:all</c>
    /// or <c>off</c>. Channel names match <see cref="GameCubeTraceChannel"/>.
    /// </summary>
    public static GameCubeTraceSettings Parse(string specification) =>
        TryParse(specification, out var settings)
            ? settings
            : throw new FormatException(
                $"\"{specification}\" is not a valid PixelCube trace specification.");

    public static bool TryParse(string? specification, out GameCubeTraceSettings settings)
    {
        settings = Default;
        if (string.IsNullOrWhiteSpace(specification))
        {
            return false;
        }

        var separator = specification.IndexOf(':', StringComparison.Ordinal);
        var levelText = (separator < 0 ? specification : specification[..separator]).Trim();
        if (!TryParseLevel(levelText, out var level))
        {
            return false;
        }

        if (level == GameCubeTraceLevel.Off)
        {
            settings = Disabled;
            return true;
        }

        if (separator < 0)
        {
            settings = new GameCubeTraceSettings(level, GameCubeTraceChannel.Default);
            return true;
        }

        var channels = GameCubeTraceChannel.None;
        foreach (var name in specification[(separator + 1)..].Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse(name, ignoreCase: true, out GameCubeTraceChannel channel))
            {
                return false;
            }

            channels |= channel;
        }

        if (channels == GameCubeTraceChannel.None)
        {
            return false;
        }

        settings = new GameCubeTraceSettings(level, channels);
        return true;
    }

    private static bool TryParseLevel(string text, out GameCubeTraceLevel level)
    {
        // The short spellings are what anyone actually types into an
        // environment variable, and Enum.TryParse would take a bare number as
        // a level, which is not a spelling worth honouring.
        switch (text.ToLowerInvariant())
        {
            case "off" or "none":
                level = GameCubeTraceLevel.Off;
                return true;
            case "err" or "error":
                level = GameCubeTraceLevel.Error;
                return true;
            case "warn" or "warning":
                level = GameCubeTraceLevel.Warning;
                return true;
            case "info" or "information":
                level = GameCubeTraceLevel.Information;
                return true;
            case "dbg" or "debug":
                level = GameCubeTraceLevel.Debug;
                return true;
            case "verb" or "verbose" or "all":
                level = GameCubeTraceLevel.Verbose;
                return true;
            default:
                level = GameCubeTraceLevel.Off;
                return false;
        }
    }
}
