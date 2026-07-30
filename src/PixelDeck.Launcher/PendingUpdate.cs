using System.Text.Json;
using System.Text.Json.Serialization;

namespace PixelDeck.Launcher;

/// <summary>One file an update intends to put into the install folder.</summary>
public sealed class PendingFile
{
    /// <summary>Path relative to the install folder, using forward slashes.</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 the staged copy must hash to.</summary>
    public string Sha256 { get; set; } = string.Empty;

    public long Length { get; set; }
}

/// <summary>
/// What the application staged and wants the next launcher start to install.
/// </summary>
/// <remarks>
/// Written by the running application and read once by the launcher, before any
/// replaceable assembly has been loaded. That ordering is the whole design: at
/// the moment the launcher acts, nothing it is about to overwrite is in use, so
/// no helper process is needed to get around a file lock.
///
/// This shape is duplicated on the application side. It is deliberately small
/// and additive-only so the two can never disagree about a field that matters.
/// </remarks>
public sealed class PendingUpdate
{
    public string StagingFolder { get; set; } = string.Empty;

    public string TargetRelease { get; set; } = string.Empty;

    public string PreviousRelease { get; set; } = string.Empty;

    /// <summary>Whether the staged payload replaces PixelDeck.exe itself.</summary>
    public bool LauncherIncluded { get; set; }

    /// <summary>The process that staged this, which must exit before we copy.</summary>
    public int? WaitForProcessId { get; set; }

    public List<PendingFile> Files { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>
    /// Reads the pending record, or null when there is nothing to do.
    /// </summary>
    /// <remarks>
    /// The file is treated as untrusted: it may be truncated by a crash mid-write
    /// or left over from a much older build, and neither may take the launch down.
    /// </remarks>
    public static PendingUpdate? Read()
    {
        try
        {
            if (!File.Exists(LauncherPaths.PendingUpdate))
            {
                return null;
            }

            var pending = JsonSerializer.Deserialize<PendingUpdate>(
                File.ReadAllText(LauncherPaths.PendingUpdate), Options);

            if (pending is null || string.IsNullOrWhiteSpace(pending.StagingFolder))
            {
                LauncherLog.Write("Pending update record was unusable; ignoring it.");
                return null;
            }

            return pending;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            LauncherLog.Write("Could not read the pending update record.", exception);
            return null;
        }
    }

    /// <summary>
    /// Records how an install turned out, for the application to report.
    /// </summary>
    /// <remarks>
    /// Without this the application's "updated successfully" and "the last update
    /// did not finish" messages never appear: it reads this file on startup, and
    /// the launcher is now the only thing in a position to write it.
    /// </remarks>
    public static void WriteOutcome(
        string targetRelease,
        string previousRelease,
        string? failure,
        string? path = null)
    {
        try
        {
            var target = path ?? LauncherPaths.UpdateOutcome;
            if (path is null)
            {
                LauncherPaths.EnsureStateFolder();
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            }

            File.WriteAllText(
                target,
                JsonSerializer.Serialize(
                    new { targetVersion = targetRelease, previousVersion = previousRelease, failure },
                    Options));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LauncherLog.Write("Could not record the update outcome.", exception);
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(LauncherPaths.PendingUpdate))
            {
                File.Delete(LauncherPaths.PendingUpdate);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LauncherLog.Write("Could not clear the pending update record.", exception);
        }
    }
}
