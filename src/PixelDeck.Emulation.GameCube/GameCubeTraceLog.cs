using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// PixelCube's trace log: the one place the emulator says what it is doing.
/// </summary>
/// <remarks>
/// Written first, before any of the hardware, because of what the other cores
/// cost to debug without it. A GameCube fails in ways that all look identical
/// from outside — a black screen is an unimplemented opcode, a bad DVD read, a
/// missing interrupt, or a framebuffer at the wrong address, and nothing on
/// the outside distinguishes them. Every hour spent guessing between those is
/// an hour this class is meant to remove.
///
/// Three properties make it usable at GameCube volume rather than only in a
/// unit test:
///
/// <list type="bullet">
/// <item>A disabled channel costs a bit test and no allocation, because the
/// message is built by <see cref="GameCubeTraceInterpolatedStringHandler"/>
/// only after the log has agreed to keep it.</item>
/// <item>Repetition is collapsed at the source.
/// <see cref="WriteOnce(GameCubeTraceChannel, GameCubeTraceLevel, string, ref GameCubeTraceInterpolatedStringHandler)"/>
/// reports the first occurrence and counts the rest, so one unimplemented
/// opcode produces one line and a tally instead of a gigabyte.</item>
/// <item>The last <see cref="RecentCapacity"/> records stay in memory whatever
/// the sinks do, so a freeze or a crash can be asked what happened
/// immediately before it.</item>
/// </list>
///
/// Records describe the emulator, not the player: disc titles and game codes
/// are fair game because they are needed to interpret anything, but file
/// paths and save contents stay out, because this file is meant to be
/// pasted into a bug report.
/// </remarks>
public sealed class GameCubeTraceLog : IDisposable
{
    /// <summary>
    /// How many records the in-memory ring keeps by default. Large enough to
    /// cover the run-up to a fault at a few thousand records per second,
    /// small enough that keeping it costs nothing worth measuring.
    /// </summary>
    public const int DefaultRecentCapacity = 1024;

    /// <summary>
    /// The ceiling on distinct <see cref="WriteOnce"/> keys. A key set that
    /// grows without bound is itself a leak, and a core that produces more
    /// than this many distinct first-occurrences has bigger problems than the
    /// ones it is failing to report.
    /// </summary>
    private const int MaximumDistinctKeys = 8192;

    /// <summary>
    /// Marks a key as describing PixelDeck observing the run rather than
    /// anything the game did. Counted like any other, but never offered as the
    /// answer to what a run is stuck on.
    /// </summary>
    public const string ObserverKeyPrefix = "session/";

    private readonly ConcurrentDictionary<string, long> _occurrences = new(StringComparer.Ordinal);
    private readonly GameCubeTraceRecord[] _recent;
    private readonly List<IGameCubeTraceSink> _sinks = [];
    private readonly List<IDisposable> _ownedSinks = [];
    private readonly Lock _gate = new();
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    private int _levelValue;
    private int _channelsValue;
    private int _recentCount;
    private int _recentNext;
    private long _sequence;
    private long _keptCount;
    private long _suppressedCount;
    private long _frame;
    private bool _reportedKeyBudget;
    private bool _disposed;

    public GameCubeTraceLog(
        GameCubeTraceSettings? settings = null,
        int recentCapacity = DefaultRecentCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recentCapacity, 1);

        var resolved = settings ?? GameCubeTraceSettings.Default;
        _recent = new GameCubeTraceRecord[recentCapacity];
        _levelValue = (int)resolved.Level;
        _channelsValue = (int)resolved.Channels;
        Settings = resolved;
    }

    /// <summary>The settings this log was created from.</summary>
    public GameCubeTraceSettings Settings { get; }

    public int RecentCapacity => _recent.Length;

    /// <summary>Records kept. Compare with <see cref="SuppressedCount"/>.</summary>
    public long KeptCount => Interlocked.Read(ref _keptCount);

    /// <summary>Records collapsed into an existing key's tally.</summary>
    public long SuppressedCount => Interlocked.Read(ref _suppressedCount);

    /// <summary>
    /// The emulated frame stamped onto new records. Set once per frame by the
    /// machine so a record can be tied to what was on screen.
    /// </summary>
    public long Frame
    {
        get => Interlocked.Read(ref _frame);
        set => Interlocked.Exchange(ref _frame, value);
    }

    public GameCubeTraceLevel Level
    {
        get => (GameCubeTraceLevel)Volatile.Read(ref _levelValue);
        set => Volatile.Write(ref _levelValue, (int)value);
    }

    public GameCubeTraceChannel Channels
    {
        get => (GameCubeTraceChannel)Volatile.Read(ref _channelsValue);
        set => Volatile.Write(ref _channelsValue, (int)value);
    }

    /// <summary>
    /// Creates a log configured from <c>PIXELCUBE_TRACE</c> and
    /// <c>PIXELCUBE_TRACE_FILE</c>, already writing to its trace file, and
    /// already carrying any complaint about how it was configured.
    /// </summary>
    public static GameCubeTraceLog CreateFromEnvironment()
    {
        var settings = GameCubeTraceSettings.FromEnvironment();
        var log = new GameCubeTraceLog(settings);
        if (settings.Level != GameCubeTraceLevel.Off && settings.FilePath is { Length: > 0 } path)
        {
            log.AddOwnedSink(new GameCubeTraceFileSink(path));
        }

        if (settings.ConfigurationWarning is { Length: > 0 } warning)
        {
            log.Write(GameCubeTraceChannel.Boot, GameCubeTraceLevel.Warning, warning);
        }

        return log;
    }

    /// <summary>
    /// Whether a record on <paramref name="channel"/> at
    /// <paramref name="level"/> would be kept. The one check every trace call
    /// makes, so it stays a pair of loads and a mask.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsEnabled(GameCubeTraceChannel channel, GameCubeTraceLevel level) =>
        level != GameCubeTraceLevel.Off &&
        (int)level <= Volatile.Read(ref _levelValue) &&
        ((int)channel & Volatile.Read(ref _channelsValue)) != 0;

    /// <summary>Adds a sink this log will not dispose.</summary>
    public void AddSink(IGameCubeTraceSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            _sinks.Add(sink);
        }
    }

    /// <summary>Adds a sink whose lifetime this log now owns.</summary>
    public void AddOwnedSink<TSink>(TSink sink)
        where TSink : IGameCubeTraceSink, IDisposable
    {
        ArgumentNullException.ThrowIfNull(sink);
        lock (_gate)
        {
            _sinks.Add(sink);
            _ownedSinks.Add(sink);
        }
    }

    public void Write(GameCubeTraceChannel channel, GameCubeTraceLevel level, string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsEnabled(channel, level))
        {
            return;
        }

        Publish(channel, level, message);
    }

    public void Write(
        GameCubeTraceChannel channel,
        GameCubeTraceLevel level,
        [InterpolatedStringHandlerArgument("", nameof(channel), nameof(level))]
        ref GameCubeTraceInterpolatedStringHandler message)
    {
        if (!message.IsEnabled)
        {
            return;
        }

        Publish(channel, level, message.ToStringAndClear());
    }

    /// <summary>
    /// Reports the first occurrence of <paramref name="key"/> and silently
    /// counts every later one. The message is not even formatted for a
    /// repeat, so this is the right call for anything inside an instruction
    /// or memory-access path.
    /// </summary>
    /// <returns>Whether this occurrence was reported.</returns>
    public bool WriteOnce(
        GameCubeTraceChannel channel,
        GameCubeTraceLevel level,
        string key,
        [InterpolatedStringHandlerArgument("", nameof(channel), nameof(level), nameof(key))]
        ref GameCubeTraceInterpolatedStringHandler message)
    {
        if (!message.IsEnabled)
        {
            return false;
        }

        Publish(channel, level, message.ToStringAndClear());
        return true;
    }

    /// <summary>
    /// The already-built-message form of
    /// <see cref="WriteOnce(GameCubeTraceChannel, GameCubeTraceLevel, string, ref GameCubeTraceInterpolatedStringHandler)"/>.
    /// </summary>
    /// <remarks>
    /// Present so that a message assembled by concatenation still compiles and
    /// still suppresses — without it, writing <c>$"a" + "b"</c> instead of one
    /// interpolated string is a type error rather than a slightly slower call.
    /// It does give up the guarantee the interpolated form provides: the
    /// message here is built before the log is asked whether it wants it, so
    /// prefer a single interpolated string on any hot path.
    /// </remarks>
    public bool WriteOnce(
        GameCubeTraceChannel channel,
        GameCubeTraceLevel level,
        string key,
        string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsEnabled(channel, level) || CountOccurrence(key) != 1)
        {
            return false;
        }

        Publish(channel, level, message);
        return true;
    }

    /// <summary>
    /// Reports the first occurrence of <paramref name="key"/> and then every
    /// <paramref name="interval"/>th one, for samples that are worth watching
    /// over time rather than seeing once — frame timings, task counts.
    /// </summary>
    public bool WriteEvery(
        GameCubeTraceChannel channel,
        GameCubeTraceLevel level,
        string key,
        long interval,
        [InterpolatedStringHandlerArgument("", nameof(channel), nameof(level), nameof(key), nameof(interval))]
        ref GameCubeTraceInterpolatedStringHandler message)
    {
        if (!message.IsEnabled)
        {
            return false;
        }

        Publish(channel, level, message.ToStringAndClear());
        return true;
    }

    /// <summary>
    /// The already-built-message form of
    /// <see cref="WriteEvery(GameCubeTraceChannel, GameCubeTraceLevel, string, long, ref GameCubeTraceInterpolatedStringHandler)"/>.
    /// The same trade-off applies: it works, but the message is built whether
    /// or not this occurrence is due.
    /// </summary>
    public bool WriteEvery(
        GameCubeTraceChannel channel,
        GameCubeTraceLevel level,
        string key,
        long interval,
        string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!IsEnabled(channel, level))
        {
            return false;
        }

        var occurrence = CountOccurrence(key);
        if (occurrence <= 0 || (interval > 1 && (occurrence - 1) % interval != 0))
        {
            return false;
        }

        Publish(channel, level, message);
        return true;
    }

    /// <summary>
    /// The last records this log kept, oldest first. Survives whatever the
    /// sinks did, which is the point: a sink can be full, slow, or absent, and
    /// this still answers "what happened just before the freeze".
    /// </summary>
    public IReadOnlyList<GameCubeTraceRecord> CaptureRecent()
    {
        lock (_gate)
        {
            var captured = new GameCubeTraceRecord[_recentCount];
            var start = _recentCount == _recent.Length ? _recentNext : 0;
            for (var index = 0; index < _recentCount; index++)
            {
                captured[index] = _recent[(start + index) % _recent.Length];
            }

            return captured;
        }
    }

    /// <summary>
    /// Every repeated key and how often it was seen, most frequent first.
    /// This is the work list a session produces: the opcode hit four million
    /// times is the one to implement next.
    /// </summary>
    public IReadOnlyList<GameCubeTraceCounter> CaptureCounters() =>
        _occurrences
            .Select(entry => new GameCubeTraceCounter(entry.Key, entry.Value))
            .OrderByDescending(counter => counter.Count)
            .ThenBy(counter => counter.Key, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// The single most-repeated key, or an empty counter when nothing has
    /// repeated. Answers "what is it waiting on" without sorting the whole
    /// table, which matters because a live display asks once a second.
    /// </summary>
    public GameCubeTraceCounter BusiestCounter()
    {
        var busiest = new GameCubeTraceCounter(string.Empty, 0);
        foreach (var entry in _occurrences)
        {
            // Observer keys are excluded. A once-a-frame heartbeat outnumbers
            // everything early in a run, so without this the answer to "what
            // is it waiting on" is PixelDeck watching itself.
            if (entry.Key.StartsWith(ObserverKeyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.Value > busiest.Count)
            {
                busiest = new GameCubeTraceCounter(entry.Key, entry.Value);
            }
        }

        return busiest;
    }

    /// <summary>
    /// Writes the tally of repeated keys, so a run ends with the list of what
    /// it kept hitting rather than only the first time it hit each one.
    /// </summary>
    public void WriteCounterSummary(
        GameCubeTraceChannel channel = GameCubeTraceChannel.Unimplemented,
        int maximumEntries = 32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumEntries, 1);
        if (!IsEnabled(channel, GameCubeTraceLevel.Information))
        {
            return;
        }

        var counters = CaptureCounters();
        if (counters.Count == 0)
        {
            return;
        }

        Publish(
            channel,
            GameCubeTraceLevel.Information,
            $"trace summary: {counters.Count} distinct keys, " +
            $"{KeptCount} records kept, {SuppressedCount} repeats collapsed");
        foreach (var counter in counters.Take(maximumEntries))
        {
            Publish(
                channel,
                GameCubeTraceLevel.Information,
                $"  {counter.Count,12:N0}  {counter.Key}");
        }
    }

    /// <summary>
    /// Counts an occurrence of <paramref name="key"/> and returns its new
    /// total, or zero when the key budget is exhausted. Called by the
    /// interpolated string handler before it decides to format anything.
    /// </summary>
    internal long CountOccurrence(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (_occurrences.TryGetValue(key, out _) || _occurrences.Count < MaximumDistinctKeys)
        {
            var count = _occurrences.AddOrUpdate(key, 1L, static (_, existing) => existing + 1);
            if (count > 1)
            {
                Interlocked.Increment(ref _suppressedCount);
            }

            return count;
        }

        Interlocked.Increment(ref _suppressedCount);
        ReportKeyBudgetOnce();
        return 0;
    }

    public void Flush()
    {
        foreach (var sink in SnapshotSinks())
        {
            sink.Flush();
        }
    }

    public void Dispose()
    {
        List<IDisposable> owned;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owned = [.. _ownedSinks];
            _ownedSinks.Clear();
            _sinks.Clear();
        }

        foreach (var sink in owned)
        {
            sink.Dispose();
        }
    }

    private void Publish(
        GameCubeTraceChannel channel,
        GameCubeTraceLevel level,
        string message)
    {
        var record = new GameCubeTraceRecord(
            Interlocked.Increment(ref _sequence),
            Frame,
            Stopwatch.GetElapsedTime(_startTimestamp),
            channel,
            level,
            message);

        Interlocked.Increment(ref _keptCount);

        IGameCubeTraceSink[] sinks;
        lock (_gate)
        {
            _recent[_recentNext] = record;
            _recentNext = (_recentNext + 1) % _recent.Length;
            if (_recentCount < _recent.Length)
            {
                _recentCount++;
            }

            sinks = [.. _sinks];
        }

        foreach (var sink in sinks)
        {
            sink.Write(record);
        }
    }

    private IGameCubeTraceSink[] SnapshotSinks()
    {
        lock (_gate)
        {
            return [.. _sinks];
        }
    }

    private void ReportKeyBudgetOnce()
    {
        lock (_gate)
        {
            if (_reportedKeyBudget)
            {
                return;
            }

            _reportedKeyBudget = true;
        }

        // Deliberately not routed through WriteOnce: the key table is the
        // thing that just filled up.
        Write(
            GameCubeTraceChannel.Unimplemented,
            GameCubeTraceLevel.Warning,
            $"trace key budget of {MaximumDistinctKeys} distinct keys is exhausted; " +
            "further first occurrences are counted but not reported");
    }
}
