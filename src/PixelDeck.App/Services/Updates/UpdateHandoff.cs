using System.Diagnostics;

namespace PixelDeck.App.Services.Updates;

/// <summary>
/// Hands a verified, staged update over to PixelDeck.Updater and reports
/// whether PixelDeck should now shut down.
/// </summary>
/// <remarks>
/// The installer has to run outside PixelDeck because it overwrites the files
/// PixelDeck is executing from. Pending state is written first, so that even if
/// the updater dies mid-way the next launch can tell the update did not apply.
/// </remarks>
public static class UpdateHandoff
{
    /// <summary>Where PixelDeck is installed, i.e. the folder to be replaced.</summary>
    public static string InstallFolder => AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string UpdaterPath => Path.Combine(
        InstallFolder,
        OperatingSystem.IsWindows() ? "PixelDeck.Updater.exe" : "PixelDeck.Updater");

    /// <summary>
    /// Copies the updater out of the install folder and returns the copy, or
    /// null if it could not be staged.
    /// </summary>
    /// <remarks>
    /// The updater must not run from the folder it is about to replace.
    /// Windows refuses to overwrite the image of a running process, so an
    /// updater started in place fails as soon as the copy reaches its own
    /// executable — and because that failure is indistinguishable from any
    /// other, it rolls the whole update back. Running from a temporary copy
    /// leaves every file in the install folder replaceable, including the
    /// updater itself.
    /// </remarks>
    public static string? StageUpdater(string updaterPath)
    {
        try
        {
            var folder = Path.Combine(Path.GetTempPath(), $"PixelDeck-updater-{Guid.NewGuid():N}");
            Directory.CreateDirectory(folder);

            var destination = Path.Combine(folder, Path.GetFileName(updaterPath));
            File.Copy(updaterPath, destination, overwrite: true);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    destination,
                    File.GetUnixFileMode(destination) |
                    UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
            }

            return destination;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            UpdateDiagnostics.Write("The updater could not be staged for launch.", exception);
            return null;
        }
    }

    /// <summary>
    /// Starts the installer. Returns false when it could not be launched, in
    /// which case the caller should carry on into the current version.
    /// </summary>
    public static bool TryStart(StagedUpdate staged, Version runningVersion, UpdatePlatform? platform = null)
    {
        var resolved = platform ?? UpdatePlatform.Current;

        if (!File.Exists(UpdaterPath))
        {
            UpdateDiagnostics.Write($"Updater not found at {UpdaterPath}; staying on the current version.");
            return false;
        }

        if (StageUpdater(UpdaterPath) is not { } updaterToRun)
        {
            return false;
        }

        UpdateStateStore.Write(new PendingUpdateState
        {
            TargetVersion = staged.Release.Version.ToString(),
            PreviousVersion = runningVersion.ToString()
        });

        try
        {
            var start = new ProcessStartInfo(updaterToRun)
            {
                WorkingDirectory = InstallFolder,
                UseShellExecute = false
            };
            start.ArgumentList.Add("--staging");
            start.ArgumentList.Add(staged.StagingFolder);
            start.ArgumentList.Add("--install");
            start.ArgumentList.Add(InstallFolder);
            start.ArgumentList.Add("--executable");
            start.ArgumentList.Add(resolved.ExecutableName);
            start.ArgumentList.Add("--from");
            start.ArgumentList.Add(runningVersion.ToString());
            start.ArgumentList.Add("--to");
            start.ArgumentList.Add(staged.Release.Version.ToString());
            start.ArgumentList.Add("--wait-for");
            start.ArgumentList.Add(Environment.ProcessId.ToString());

            Process.Start(start);
            UpdateDiagnostics.Write($"Handed {staged.Release.Version} to the updater; shutting down.");
            return true;
        }
        catch (Exception exception)
        {
            UpdateDiagnostics.Write("The updater could not be started.", exception);
            // Nothing was replaced, so clear the pending record rather than
            // leaving the next launch reporting a phantom failure.
            UpdateStateStore.Clear();
            return false;
        }
    }

    /// <summary>
    /// Reads the version this build was told it upgraded from, when relaunched
    /// by the updater as <c>--updated-from &lt;version&gt;</c>.
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
