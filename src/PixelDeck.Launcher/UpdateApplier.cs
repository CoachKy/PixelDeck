using System.Diagnostics;
using System.Security.Cryptography;

namespace PixelDeck.Launcher;

/// <summary>What happened when the launcher tried to install a staged update.</summary>
public sealed record ApplyOutcome(bool Applied, bool LauncherReplaced, string? Failure)
{
    public static ApplyOutcome Nothing { get; } = new(false, false, null);
}

/// <summary>
/// Installs a staged update into the install folder before anything replaceable
/// has been loaded.
/// </summary>
/// <remarks>
/// Runs inside the launcher rather than a helper process. At this point in
/// startup no component assembly is loaded, so none of the files being replaced
/// is locked, which is what previously forced a separate updater executable to
/// exist purely to work around a lock.
///
/// The staged payload is treated as untrusted throughout: paths are checked for
/// traversal, restricted to files PixelDeck owns, and hashed before anything is
/// overwritten. Nothing is executed from the staging folder.
/// </remarks>
public sealed class UpdateApplier(string installFolder, Action<string> log)
{
    private string ComponentFolder => Path.Combine(installFolder, "Components");

    private string BackupFolder => Path.Combine(installFolder, ".backup");

    /// <summary>
    /// Files an update is permitted to write, relative to the install folder.
    /// </summary>
    /// <remarks>
    /// An allowlist rather than a denylist: a release manifest is remote input,
    /// and the failure mode of guessing wrong is overwriting something outside
    /// PixelDeck. User content is not on this list, so an update can never
    /// touch a save, a ROM or a library image.
    /// </remarks>
    private static bool IsReplaceable(string relativePath)
    {
        if (relativePath.StartsWith("Components/", StringComparison.OrdinalIgnoreCase))
        {
            return relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }

        if (relativePath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return relativePath is "PixelDeck.exe" or "PixelDeck"
            || relativePath.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
            || relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a manifest-supplied relative path against the install folder,
    /// returning null when it escapes or is not ours to write.
    /// </summary>
    private string? ResolveTarget(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) ||
            Path.IsPathRooted(relativePath) ||
            relativePath.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        var normalized = relativePath.Replace('\\', '/');
        if (!IsReplaceable(normalized))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(installFolder, normalized));
        var root = Path.GetFullPath(installFolder) + Path.DirectorySeparatorChar;

        // Belt and braces: even with the checks above, confirm the resolved
        // path really is inside the install folder before writing to it.
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public ApplyOutcome Apply(PendingUpdate pending)
    {
        if (pending.Files.Count == 0)
        {
            log("Pending update listed no files; nothing to install.");
            return ApplyOutcome.Nothing;
        }

        WaitForExit(pending.WaitForProcessId);

        // Validate the whole payload before touching the install folder, so a
        // bad manifest cannot leave it half-updated.
        var planned = new List<(string Staged, string Target, string Relative)>();
        foreach (var file in pending.Files)
        {
            var target = ResolveTarget(file.RelativePath);
            if (target is null)
            {
                return Fail($"Update rejected: '{file.RelativePath}' is not a file PixelDeck may replace.");
            }

            var staged = Path.Combine(pending.StagingFolder, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(staged))
            {
                return Fail($"Update rejected: staged file missing for '{file.RelativePath}'.");
            }

            if (file.Length > 0 && new FileInfo(staged).Length != file.Length)
            {
                return Fail($"Update rejected: '{file.RelativePath}' is not the expected size.");
            }

            var actual = Hash(staged);
            if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return Fail($"Update rejected: '{file.RelativePath}' failed hash verification.");
            }

            planned.Add((staged, target, file.RelativePath));
        }

        log($"Verified {planned.Count} staged file(s) for {pending.TargetRelease}.");

        var launcherReplaced = false;
        try
        {
            Backup(planned);

            foreach (var (staged, target, relative) in planned)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                // The running launcher's own image cannot be overwritten, but it
                // can be renamed. Moving it aside frees the name so the new one
                // can be written; the stale copy is swept up on the next start.
                if (IsLauncher(relative) && File.Exists(target))
                {
                    var retired = target + ".old";
                    if (File.Exists(retired))
                    {
                        TryDelete(retired);
                    }

                    File.Move(target, retired);
                    launcherReplaced = true;
                }

                File.Copy(staged, target, overwrite: true);
                EnsureExecutable(target, relative);
            }

            if (!File.Exists(Path.Combine(ComponentFolder, "PixelDeck.App.dll")))
            {
                throw new InvalidOperationException("the application assembly is missing after the copy");
            }

            log($"Installed {pending.TargetRelease} over {pending.PreviousRelease}.");
            TryDeleteFolder(BackupFolder);
            return new ApplyOutcome(true, launcherReplaced, null);
        }
        catch (Exception exception)
        {
            log($"Install failed: {exception.GetType().Name}: {exception.Message}");
            var restored = Restore();
            return new ApplyOutcome(
                false,
                false,
                restored
                    ? "The update could not be installed and the previous version was restored."
                    : "The update could not be installed and the previous version could not be fully restored.");
        }
    }

    private static bool IsLauncher(string relativePath) =>
        relativePath is "PixelDeck.exe" or "PixelDeck";

    private ApplyOutcome Fail(string message)
    {
        log(message);
        return new ApplyOutcome(false, false, message);
    }

    /// <summary>Copies aside only what is about to be overwritten.</summary>
    private void Backup(List<(string Staged, string Target, string Relative)> planned)
    {
        TryDeleteFolder(BackupFolder);
        Directory.CreateDirectory(BackupFolder);

        foreach (var (_, target, relative) in planned)
        {
            if (!File.Exists(target))
            {
                continue;
            }

            var copy = Path.Combine(BackupFolder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(target, copy, overwrite: true);
        }
    }

    private bool Restore()
    {
        if (!Directory.Exists(BackupFolder))
        {
            return false;
        }

        try
        {
            foreach (var saved in Directory.GetFiles(BackupFolder, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(BackupFolder, saved);
                var target = Path.Combine(installFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                if (IsLauncher(relative.Replace('\\', '/')) && File.Exists(target))
                {
                    // The new launcher was already written; retire it the same
                    // way so the restored one can take the name back.
                    File.Move(target, target + ".failed", overwrite: true);
                }

                File.Copy(saved, target, overwrite: true);
            }

            log("Previous version restored.");
            TryDeleteFolder(BackupFolder);
            return true;
        }
        catch (Exception exception)
        {
            log($"Restore failed: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private void WaitForExit(int? processId)
    {
        if (processId is not { } id || id == Environment.ProcessId)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(id);
            log($"Waiting for the previous PixelDeck (pid {id}) to exit.");
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
            {
                log("It did not exit within 30 seconds; continuing anyway.");
            }
        }
        catch (ArgumentException)
        {
            // Already gone, which is the normal case.
        }
    }

    /// <summary>
    /// Removes what the pre-launcher layout left behind.
    /// </summary>
    /// <remarks>
    /// Releases up to 1.22.072 put PixelDeck.App.exe, PixelDeck.Updater.exe and
    /// the managed assemblies loose in the install folder. An update copies files
    /// in but never deletes, so without this the old executable would sit beside
    /// the new launcher and a player could easily start the stale one.
    /// Only files PixelDeck itself shipped are removed; native libraries and
    /// anything under Games, Saves or Library are left alone.
    /// </remarks>
    public void CleanUpPreviousLayout()
    {
        try
        {
            foreach (var stale in new[] { "PixelDeck.App.exe", "PixelDeck.Updater.exe", "PixelDeck.App", "PixelDeck.Updater" })
            {
                var path = Path.Combine(installFolder, stale);
                if (File.Exists(path))
                {
                    log($"Removing {stale} from the previous layout.");
                    TryDelete(path);
                }
            }

            // Managed assemblies now live in Components; loose copies at the root
            // are leftovers. Native libraries keep their own names and are not
            // matched by this pattern.
            foreach (var loose in Directory.GetFiles(installFolder, "PixelDeck.*.dll", SearchOption.TopDirectoryOnly))
            {
                log($"Removing {Path.GetFileName(loose)} from the install root.");
                TryDelete(loose);
            }

            foreach (var retired in Directory.GetFiles(installFolder, "*.old", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(installFolder, "*.failed", SearchOption.TopDirectoryOnly)))
            {
                TryDelete(retired);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log($"Could not finish cleaning the previous layout: {exception.Message}");
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void EnsureExecutable(string path, string relativePath)
    {
        if (OperatingSystem.IsWindows() || !IsLauncher(relativePath))
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
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFolder(string folder)
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
        }
    }
}
