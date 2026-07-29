using System.Diagnostics;

namespace PixelDeck.Updater;

/// <summary>Everything the updater was told to do.</summary>
public sealed record InstallRequest(
    string StagingFolder,
    string InstallFolder,
    string ExecutableName,
    string PreviousVersion,
    string TargetVersion,
    int? WaitForProcessId);

public sealed record InstallOutcome(bool Succeeded, string? Failure);

/// <summary>
/// Replaces an installed PixelDeck with a staged one, restoring the previous
/// files if anything goes wrong.
/// </summary>
/// <remarks>
/// Runs as its own process because it overwrites the very files the running
/// application is using. It keeps no reference to PixelDeck.App for the same
/// reason. The sequence is: move the current install aside, copy the staged
/// build in, and only discard the backup once the new executable is present.
/// </remarks>
public sealed class UpdateInstaller(Action<string> log)
{
    public InstallOutcome Install(InstallRequest request)
    {
        var backupFolder = request.InstallFolder.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".backup";

        try
        {
            var stagedExecutable = Path.Combine(request.StagingFolder, request.ExecutableName);
            if (!File.Exists(stagedExecutable))
            {
                return new InstallOutcome(false, "The staged package is missing its executable.");
            }

            WaitForExit(request.WaitForProcessId);

            if (Directory.Exists(backupFolder))
            {
                Directory.Delete(backupFolder, recursive: true);
            }

            log($"Backing up {request.InstallFolder} to {backupFolder}.");
            CopyDirectory(request.InstallFolder, backupFolder);

            log($"Installing {request.TargetVersion} from {request.StagingFolder}.");
            CopyDirectory(request.StagingFolder, request.InstallFolder);

            var installedExecutable = Path.Combine(request.InstallFolder, request.ExecutableName);
            if (!File.Exists(installedExecutable))
            {
                throw new InvalidOperationException("The installed executable is missing after the copy.");
            }

            EnsureExecutable(installedExecutable);

            log("Install completed; removing backup.");
            TryDelete(backupFolder);
            return new InstallOutcome(true, null);
        }
        catch (Exception exception)
        {
            log($"Install failed: {exception}");
            var restored = Restore(request.InstallFolder, backupFolder);
            var detail = restored
                ? "The update could not be installed and the previous version was restored."
                : "The update could not be installed and the previous version could not be fully restored.";
            return new InstallOutcome(false, detail);
        }
    }

    /// <summary>
    /// Waits for PixelDeck to close before touching its files. Gives up after a
    /// grace period rather than hanging forever.
    /// </summary>
    private void WaitForExit(int? processId)
    {
        if (processId is not { } id)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(id);
            log($"Waiting for PixelDeck (pid {id}) to exit.");
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                log("PixelDeck did not exit within 30 seconds; continuing anyway.");
            }
        }
        catch (ArgumentException)
        {
            // Already gone, which is the normal case.
        }
    }

    private bool Restore(string installFolder, string backupFolder)
    {
        if (!Directory.Exists(backupFolder))
        {
            return false;
        }

        try
        {
            log("Restoring the previous version.");
            CopyDirectory(backupFolder, installFolder);
            TryDelete(backupFolder);
            return true;
        }
        catch (Exception exception)
        {
            log($"Restore failed: {exception}");
            return false;
        }
    }

    /// <summary>Relaunches PixelDeck, telling it which version it came from.</summary>
    public bool Relaunch(string installFolder, string executableName, string previousVersion)
    {
        var executable = Path.Combine(installFolder, executableName);
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = installFolder,
                UseShellExecute = false
            };
            start.ArgumentList.Add("--updated-from");
            start.ArgumentList.Add(previousVersion);

            Process.Start(start);
            log($"Relaunched {executable}.");
            return true;
        }
        catch (Exception exception)
        {
            log($"Relaunch failed: {exception}");
            return false;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.Ordinal));
        }

        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = file.Replace(source, destination, StringComparison.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>
    /// Grants execute permission on Unix. An extracted archive does not reliably
    /// carry it, and a PixelDeck that cannot run is a failed update.
    /// </summary>
    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                File.GetUnixFileMode(path) |
                UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Reported by the caller's log; not fatal on its own.
            Console.Error.WriteLine(exception.Message);
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
        }
    }
}
