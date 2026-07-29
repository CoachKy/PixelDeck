using System.Text;

namespace PixelDeck.App.Services.Updates;

/// <summary>
/// Append-only log for update activity.
/// </summary>
/// <remarks>
/// The splash screen only ever shows short, plain messages; anything with an
/// exception, HTTP status or GitHub payload in it belongs here instead, so a
/// failed update never puts raw diagnostics in front of the player.
/// </remarks>
public static class UpdateDiagnostics
{
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixelDeck",
        "update-diagnostics.log");

    public static void Write(string message, Exception? exception = null)
    {
        var entry = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"))
            .Append("  ")
            .Append(message);

        if (exception is not null)
        {
            entry.AppendLine().Append(exception);
        }

        entry.AppendLine();

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, entry.ToString());
            }
        }
        catch (Exception writeFailure) when (writeFailure is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never be the reason startup fails.
            System.Diagnostics.Debug.WriteLine(entry.ToString());
        }
    }
}
