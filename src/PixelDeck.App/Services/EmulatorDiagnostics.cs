using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace PixelDeck.App.Services;

/// <summary>
/// Append-only log for decisions the emulator makes that are otherwise
/// invisible.
/// </summary>
/// <remarks>
/// Written because the N64 graphics backend falls back from paraLLEl-RDP to the
/// software renderer silently: the reason was recorded into
/// <c>N64Machine.GraphicsBackendStatus</c> and then never read by anything, so a
/// working native renderer and a fallback looked identical from the outside and
/// could only be told apart by guessing from the frame rate.
///
/// Records what PixelDeck chose and why. ROM paths, save locations and anything
/// identifying what the player owns stay out, because this is a file people
/// paste into bug reports.
///
/// This is enabled in release builds, so no call may touch the disk on the
/// thread that called it: <see cref="Write"/> only formats and enqueues, and a
/// single background writer owns the file. An emulator that stutters because it
/// is describing itself is worse than one that says nothing.
/// </remarks>
public static class EmulatorDiagnostics
{
    private const long MaximumBytes = 256 * 1024;
    private const int MaximumQueuedLines = 4096;

    private static readonly BlockingCollection<string> Pending = new(MaximumQueuedLines);
    private static readonly Lazy<Task> Writer = new(StartWriter, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixelDeck",
        "emulator.log");

    public static void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {message}";
        Debug.WriteLine(line);

        _ = Writer.Value;

        try
        {
            // Never block the caller. A full queue means the writer is wedged
            // or the disk has stalled, and dropping diagnostics is always
            // preferable to stalling emulation for them.
            Pending.TryAdd(line);
        }
        catch (InvalidOperationException)
        {
            // The queue was closed during shutdown. Nothing left to write to.
        }
    }

    /// <summary>
    /// Drains anything still queued. Called on shutdown so the last lines
    /// before a crash or a quit are not the ones that get lost.
    /// </summary>
    public static void Flush()
    {
        if (!Writer.IsValueCreated)
        {
            return;
        }

        // Wait for the queue to drain rather than closing it. Closing would
        // make every later Write throw, so a flush anywhere but the very last
        // moment of shutdown would silently end logging for the session.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (Pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }
    }

    private static Task StartWriter() =>
        Task.Factory.StartNew(
            DrainPendingLines,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static void DrainPendingLines()
    {
        // Trimming has to happen with the log closed, so the writer runs as a
        // loop: each pass owns the file until it fills, then releases it,
        // trims, and reopens. Trimming while the stream was still open threw,
        // which killed this thread and stopped logging for the whole session.
        while (!Pending.IsCompleted)
        {
            if (!WriteUntilLogIsFull())
            {
                return;
            }

            TrimOldestLines();
        }
    }

    /// <summary>
    /// Writes queued lines until the queue completes or the log outgrows its
    /// budget. Returns true when the log needs trimming before writing can
    /// continue, false when there is nothing further to write.
    /// </summary>
    private static bool WriteUntilLogIsFull()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            using var stream = new FileStream(
                LogPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = false };

            foreach (var line in Pending.GetConsumingEnumerable())
            {
                writer.WriteLine(line);

                // Flush per batch rather than per line: the queue is usually
                // empty between samples, so this still lands on disk promptly
                // while a burst costs one flush instead of one per line.
                if (Pending.Count == 0)
                {
                    writer.Flush();
                }

                if (stream.Length > MaximumBytes)
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
            // Diagnostics must never be the reason a game fails to start.
            return false;
        }
    }

    /// <summary>
    /// Drops the oldest half of the log rather than emptying it, because
    /// wiping discards the cartridge and backend lines written at load — the
    /// ones that say what was running when a long session went wrong. This runs
    /// on the writer thread, never on a caller's.
    /// </summary>
    private static void TrimOldestLines()
    {
        try
        {
            var kept = File.ReadAllLines(LogPath);
            File.WriteAllLines(LogPath, kept.Skip(kept.Length / 2), Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // If the log cannot be trimmed it simply keeps growing, which is a
            // far better outcome than the writer giving up on the session.
        }
    }
}
