using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace PixelDeck.App.Services.Updates;

/// <summary>
/// Records a staged update for the launcher to install, then reports whether
/// PixelDeck should shut down so it can.
/// </summary>
/// <remarks>
/// No helper executable is involved. The launcher installs the update during its
/// own startup, before it loads any replaceable assembly, so nothing being
/// replaced is in use at that moment. That is why PixelDeck.Updater.exe no
/// longer exists: its entire purpose was working around a file lock this
/// ordering avoids.
///
/// The record's shape is fixed by the launcher, which parses it defensively.
/// Fields may be added but not renamed.
/// </remarks>
public static class UpdateHandoff
{
    /// <summary>Where PixelDeck is installed.</summary>
    public static string InstallFolder => AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>The launcher, which is also the process that installs updates.</summary>
    public static string LauncherPath => Path.Combine(
        InstallFolder,
        OperatingSystem.IsWindows() ? "PixelDeck.exe" : "PixelDeck");

    private static readonly JsonSerializerOptions RecordOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static string PendingUpdatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixelDeck",
        "pending-components.json");

    /// <summary>
    /// Describes the staged payload for the launcher: every file, with the hash
    /// it must still have when the launcher gets to it.
    /// </summary>
    /// <remarks>
    /// Hashing here as well as at download time is deliberate. The staged copy
    /// sits on disk across a process restart, and the launcher verifies against
    /// this record rather than trusting that nothing touched it in between.
    /// </remarks>
    public static object BuildPendingRecord(
        string stagingFolder,
        string targetRelease,
        string previousRelease,
        int? waitForProcessId)
    {
        var files = new List<object>();
        var launcherIncluded = false;
        var launcherName = Path.GetFileName(LauncherPath);

        foreach (var staged in Directory.GetFiles(stagingFolder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(stagingFolder, staged).Replace('\\', '/');

            if (string.Equals(relative, launcherName, StringComparison.OrdinalIgnoreCase))
            {
                launcherIncluded = true;
            }

            using var stream = File.OpenRead(staged);
            files.Add(new
            {
                relativePath = relative,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(stream)),
                length = new FileInfo(staged).Length
            });
        }

        return new
        {
            stagingFolder,
            targetRelease,
            previousRelease,
            launcherIncluded,
            waitForProcessId,
            files
        };
    }

    /// <summary>
    /// Hands the staged update to the launcher and starts it. Returns false when
    /// that could not be arranged, in which case the caller carries on into the
    /// current version.
    /// </summary>
    public static bool TryStart(StagedUpdate staged, Version runningVersion)
    {
        if (!File.Exists(LauncherPath))
        {
            UpdateDiagnostics.Write($"Launcher not found at {LauncherPath}; staying on the current version.");
            return false;
        }

        if (!Directory.Exists(staged.StagingFolder))
        {
            UpdateDiagnostics.Write("Staging folder is missing; staying on the current version.");
            return false;
        }

        try
        {
            var record = BuildPendingRecord(
                staged.StagingFolder,
                staged.Release.Version.ToString(),
                runningVersion.ToString(),
                Environment.ProcessId);

            Directory.CreateDirectory(Path.GetDirectoryName(PendingUpdatePath)!);
            File.WriteAllText(
                PendingUpdatePath,
                JsonSerializer.Serialize(record, RecordOptions));

            // A second PixelDeck starts, waits for this one to exit, installs the
            // update and carries on. Both processes run the same executable,
            // which Windows permits.
            var start = new ProcessStartInfo(LauncherPath)
            {
                WorkingDirectory = InstallFolder,
                UseShellExecute = false
            };
            start.ArgumentList.Add("--updated-from");
            start.ArgumentList.Add(runningVersion.ToString());

            Process.Start(start);
            UpdateDiagnostics.Write($"Staged {staged.Release.Version} for the launcher; shutting down.");
            return true;
        }
        catch (Exception exception)
        {
            UpdateDiagnostics.Write("The update could not be handed to the launcher.", exception);
            TryClear();
            return false;
        }
    }

    private static void TryClear()
    {
        try
        {
            if (File.Exists(PendingUpdatePath))
            {
                File.Delete(PendingUpdatePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing was replaced, so a stale record is the worst case and the
            // launcher rejects one it cannot use.
        }
    }

    /// <summary>
    /// Reads the version this build was told it upgraded from, when relaunched
    /// as <c>--updated-from &lt;version&gt;</c>.
    /// </summary>
    public static string? ReadUpdatedFromArgument(string[] arguments)
    {
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (string.Equals(arguments[index], "--updated-from", StringComparison.Ordinal))
            {
                return arguments[index + 1];
            }
        }

        return null;
    }
}
