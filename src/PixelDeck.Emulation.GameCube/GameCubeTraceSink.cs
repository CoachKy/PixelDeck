using System.Collections.Concurrent;
using System.Text;

namespace PixelDeck.Emulation.GameCube;

/// <summary>
/// Somewhere trace records go. Implementations are called from whichever
/// thread produced the record — usually the emulation thread — so they must
/// not block it.
/// </summary>
public interface IGameCubeTraceSink
{
    void Write(in GameCubeTraceRecord record);

    /// <summary>Pushes anything buffered as far as it can go. May block.</summary>
    void Flush();
}

/// <summary>
/// Hands each record to a callback. This is how the dashboard forwards
/// PixelCube's trace into its own <c>emulator.log</c> without the emulation
/// assembly having to know that the dashboard exists.
/// </summary>
public sealed class GameCubeTraceDelegateSink : IGameCubeTraceSink
{
    private readonly Action<GameCubeTraceRecord> _write;
    private readonly Action? _flush;

    public GameCubeTraceDelegateSink(Action<GameCubeTraceRecord> write, Action? flush = null)
    {
        ArgumentNullException.ThrowIfNull(write);
        _write = write;
        _flush = flush;
    }

    public void Write(in GameCubeTraceRecord record) => _write(record);

    public void Flush() => _flush?.Invoke();
}

/// <summary>
/// Writes trace records to a file from a background thread.
/// </summary>
/// <remarks>
/// Follows the same rules as the dashboard's <c>EmulatorDiagnostics</c>,
/// for the same reasons: a trace call must never touch the disk on the thread
/// that made it, a full queue drops records rather than stalling emulation,
/// and the file is trimmed by dropping its oldest half rather than by
/// emptying it — the lines written at startup say which disc was running and
/// are the last thing worth discarding.
/// </remarks>
public sealed class GameCubeTraceFileSink : IGameCubeTraceSink, IDisposable
{
    private const long DefaultMaximumBytes = 8L * 1024 * 1024;
    private const int MaximumQueuedRecords = 16384;

    private readonly BlockingCollection<string> _pending = new(MaximumQueuedRecords);
    private readonly Task _writer;
    private readonly long _maximumBytes;
    private long _droppedRecords;
    private bool _disposed;

    public GameCubeTraceFileSink(string path, long maximumBytes = DefaultMaximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumBytes, 0);

        FilePath = Path.GetFullPath(path);
        _maximumBytes = maximumBytes;
        _writer = Task.Factory.StartNew(
            DrainPendingLines,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public string FilePath { get; }

    /// <summary>
    /// Records thrown away because the queue was full. A non-zero value means
    /// the trace has gaps, so it is reported rather than hidden.
    /// </summary>
    public long DroppedRecords => Interlocked.Read(ref _droppedRecords);

    public void Write(in GameCubeTraceRecord record)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_pending.TryAdd(record.Format()))
            {
                Interlocked.Increment(ref _droppedRecords);
            }
        }
        catch (InvalidOperationException)
        {
            // The queue was completed by a concurrent Dispose.
        }
    }

    public void Flush()
    {
        if (_disposed)
        {
            return;
        }

        // Wait for the queue to drain rather than completing it: completing
        // would end logging for the rest of the session, and a flush is
        // usually a checkpoint, not a shutdown.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (_pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pending.CompleteAdding();
        _writer.Wait(TimeSpan.FromSeconds(5));
        _pending.Dispose();
    }

    private void DrainPendingLines()
    {
        // Trimming needs the file closed, so the writer runs as a loop: each
        // pass owns the file until it fills, then releases it, trims, and
        // reopens.
        while (!_pending.IsCompleted)
        {
            if (!WriteUntilFileIsFull())
            {
                return;
            }

            TrimOldestLines();
        }
    }

    private bool WriteUntilFileIsFull()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var stream = new FileStream(
                FilePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };

            foreach (var line in _pending.GetConsumingEnumerable())
            {
                writer.WriteLine(line);

                // One flush per burst rather than per line, so the file is
                // readable while a game runs without costing a syscall per
                // record during a storm.
                if (_pending.Count == 0)
                {
                    writer.Flush();
                }

                if (stream.Length > _maximumBytes)
                {
                    writer.Flush();
                    return true;
                }
            }

            writer.Flush();
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Tracing must never be the reason a game fails to run.
            return false;
        }
    }

    private void TrimOldestLines()
    {
        try
        {
            var kept = File.ReadAllLines(FilePath);
            File.WriteAllLines(FilePath, kept.Skip(kept.Length / 2), Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An untrimmable file simply keeps growing, which beats the writer
            // giving up on the session.
        }
    }
}
