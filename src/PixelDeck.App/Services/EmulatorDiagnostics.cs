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
/// </remarks>
public static class EmulatorDiagnostics
{
    private const long MaximumBytes = 256 * 1024;
    private static readonly Lock Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixelDeck",
        "emulator.log");

    public static void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  {message}";
        Debug.WriteLine(line);

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

                // Truncate rather than grow without bound: this is written on
                // every launch, and a log nobody rotates becomes the largest
                // file PixelDeck owns.
                var log = new FileInfo(LogPath);
                if (log.Exists && log.Length > MaximumBytes)
                {
                    File.WriteAllText(LogPath, string.Empty);
                }

                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never be the reason a game fails to start.
        }
    }
}
