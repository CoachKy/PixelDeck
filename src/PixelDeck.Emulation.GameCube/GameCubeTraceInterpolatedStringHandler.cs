using System.Globalization;
using System.Runtime.CompilerServices;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Builds a trace message only when the log is actually going to keep it.
/// </summary>
/// <remarks>
/// This is the reason PixelCube can leave trace calls in hot code. Without it,
/// <c>trace.Write(Cpu, Verbose, $"pc={pc:X8} lr={lr:X8}")</c> formats and
/// allocates a string on every instruction and then throws it away, so the
/// cost of a disabled channel is paid millions of times a second and the only
/// way to make emulation fast is to delete the traces — which is exactly when
/// they are needed. The C# compiler rewrites an interpolated argument into
/// calls on this handler, and the constructor's <c>shouldAppend</c> result
/// tells it to skip every <c>Append</c> call, so a disabled channel costs one
/// bit test and nothing else.
///
/// The <c>key</c> and <c>interval</c> constructors extend that to repetition:
/// the decision to suppress a repeat is made before formatting, not after, so
/// an unimplemented opcode hit a million times costs a dictionary lookup a
/// million times rather than a million discarded strings.
/// </remarks>
[InterpolatedStringHandler]
public ref struct GameCubeTraceInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _builder;

    public GameCubeTraceInterpolatedStringHandler(
        int literalLength,
        int formattedCount,
        GameCubeTraceLog log,
        GameCubeTraceChannel channel,
        GameCubeTraceLevel level,
        out bool shouldAppend)
        : this(literalLength, formattedCount, log is not null && log.IsEnabled(channel, level), out shouldAppend)
    {
    }

    /// <summary>
    /// The form used by <see cref="GameCubeTraceLog.WriteOnce"/>. Counts the
    /// occurrence of <paramref name="key"/> whether or not the message is
    /// kept, so the suppressed total stays accurate.
    /// </summary>
    /// <remarks>
    /// The counting call comes before the level test and not after it, which is
    /// the whole point. Testing first short-circuits the count away, so a key
    /// logged below the current level is never tallied — and the tally is what
    /// the next piece of work is chosen from. That made every Debug-level key
    /// invisible in exactly the situation the tally exists for: the graphics
    /// decoder was reading commands correctly and reporting nothing, because
    /// its counters were one level below the threshold.
    /// </remarks>
    public GameCubeTraceInterpolatedStringHandler(
        int literalLength,
        int formattedCount,
        GameCubeTraceLog log,
        GameCubeTraceChannel channel,
        GameCubeTraceLevel level,
        string key,
        out bool shouldAppend)
        : this(
            literalLength,
            formattedCount,
            log is not null && log.CountOccurrence(key) == 1 && log.IsEnabled(channel, level),
            out shouldAppend)
    {
    }

    /// <summary>
    /// The form used by <see cref="GameCubeTraceLog.WriteEvery"/>: keeps the
    /// first occurrence of <paramref name="key"/> and then every
    /// <paramref name="interval"/>th one after it.
    /// </summary>
    public GameCubeTraceInterpolatedStringHandler(
        int literalLength,
        int formattedCount,
        GameCubeTraceLog log,
        GameCubeTraceChannel channel,
        GameCubeTraceLevel level,
        string key,
        long interval,
        out bool shouldAppend)
        : this(
            literalLength,
            formattedCount,
            log is not null && IsDue(log.CountOccurrence(key), interval) && log.IsEnabled(channel, level),
            out shouldAppend)
    {
    }

    private GameCubeTraceInterpolatedStringHandler(
        int literalLength,
        int formattedCount,
        bool isEnabled,
        out bool shouldAppend)
    {
        IsEnabled = isEnabled;
        shouldAppend = isEnabled;
        _builder = isEnabled
            ? new DefaultInterpolatedStringHandler(
                literalLength,
                formattedCount,
                CultureInfo.InvariantCulture)
            : default;
    }

    /// <summary>Whether the message was built and should be published.</summary>
    public bool IsEnabled { get; }

    public void AppendLiteral(string value) => _builder.AppendLiteral(value);

    public void AppendFormatted<T>(T value) => _builder.AppendFormatted(value);

    public void AppendFormatted<T>(T value, string? format) => _builder.AppendFormatted(value, format);

    public void AppendFormatted<T>(T value, int alignment) => _builder.AppendFormatted(value, alignment);

    public void AppendFormatted<T>(T value, int alignment, string? format) =>
        _builder.AppendFormatted(value, alignment, format);

    public void AppendFormatted(ReadOnlySpan<char> value) => _builder.AppendFormatted(value);

    public void AppendFormatted(string? value) => _builder.AppendFormatted(value);

    internal string ToStringAndClear() => IsEnabled ? _builder.ToStringAndClear() : string.Empty;

    /// <summary>
    /// A count of zero means the key budget is exhausted and the key is not
    /// being tracked, which suppresses rather than reports.
    /// </summary>
    private static bool IsDue(long occurrence, long interval) =>
        occurrence > 0 && (interval <= 1 || (occurrence - 1) % interval == 0);
}
