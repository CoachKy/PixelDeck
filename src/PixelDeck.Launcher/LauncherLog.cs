using System.Diagnostics;
using System.Text;

namespace PixelDeck.Launcher;

/// <summary>
/// Append-only trace log for everything the launcher does before the
/// application takes over.
/// </summary>
/// <remarks>
/// Deliberately records versions, decisions and outcomes only. ROM paths, save
/// locations and anything else identifying what the player owns stay out of it,
/// because this file is the one a player is most likely to paste into a bug
/// report.
/// </remarks>
internal static class LauncherLog
{
    private const long MaximumBytes = 256 * 1024;
    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {message}";
        Debug.WriteLine(line);

        try
        {
            lock (Gate)
            {
                LauncherPaths.EnsureStateFolder();

                // Truncate rather than grow without bound: this runs on every
                // launch, and a log nobody rotates eventually becomes the
                // largest file PixelDeck owns.
                var log = new FileInfo(LauncherPaths.TraceLog);
                if (log.Exists && log.Length > MaximumBytes)
                {
                    File.WriteAllText(LauncherPaths.TraceLog, string.Empty);
                }

                File.AppendAllText(LauncherPaths.TraceLog, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Logging must never be the reason a launch fails.
        }
    }

    public static void Write(string message, Exception exception) =>
        Write($"{message} :: {exception.GetType().Name}: {exception.Message}");
}
