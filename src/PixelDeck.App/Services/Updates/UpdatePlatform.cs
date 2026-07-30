using System.Runtime.InteropServices;

namespace PixelDeck.App.Services.Updates;

/// <summary>
/// The platform-specific facts the updater needs: which release asset belongs
/// to this machine, what the executable is called, and whether a freshly
/// extracted file has to be made executable.
/// </summary>
/// <remarks>
/// PixelDeck targets Windows desktops and Raspberry Pi, so none of this can be
/// assumed. Linux builds ship as tarballs because zip does not carry the Unix
/// permission bits, which would leave an extracted binary unrunnable.
/// </remarks>
public sealed record UpdatePlatform(
    string RuntimeIdentifier,
    string ExecutableName,
    IReadOnlyList<string> PackageExtensions,
    bool RequiresExecutableBit)
{
    /// <summary>Extension preferred when a release publishes several packages.</summary>
    public string PreferredExtension => PackageExtensions[0];

    public static UpdatePlatform Current { get; } = Detect();

    internal static UpdatePlatform Detect()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            Architecture.X86 => "x86",
            _ => "x64"
        };

        if (OperatingSystem.IsWindows())
        {
            return new UpdatePlatform(
                $"win-{architecture}",
                "PixelDeck.exe",
                [".zip"],
                RequiresExecutableBit: false);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new UpdatePlatform(
                $"osx-{architecture}",
                "PixelDeck",
                [".tar.gz", ".zip"],
                RequiresExecutableBit: true);
        }

        // Linux, which is what a Raspberry Pi build reports.
        return new UpdatePlatform(
            $"linux-{architecture}",
            "PixelDeck",
            [".tar.gz", ".zip"],
            RequiresExecutableBit: true);
    }

    /// <summary>
    /// Whether a release asset is the package for this platform. Matching on the
    /// runtime identifier keeps a Pi from ever downloading the Windows build.
    /// </summary>
    public bool Matches(string assetName)
    {
        if (assetName.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!assetName.Contains(RuntimeIdentifier, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return PackageExtensions.Any(extension =>
            assetName.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ranks matching assets so the preferred format wins.</summary>
    public int Rank(string assetName)
    {
        for (var index = 0; index < PackageExtensions.Count; index++)
        {
            if (assetName.EndsWith(PackageExtensions[index], StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    /// <summary>
    /// Grants execute permission on Unix. Archives do not always preserve it,
    /// and a PixelDeck that cannot be executed is a failed update.
    /// </summary>
    public void EnsureExecutable(string path)
    {
        // The OS test is explicit rather than relying on the flag, so the
        // platform-compatibility analyzer can see the call is unreachable on
        // Windows.
        if (OperatingSystem.IsWindows() || !RequiresExecutableBit || !File.Exists(path))
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(
                path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            UpdateDiagnostics.Write($"Could not set the execute bit on {path}.", exception);
        }
    }
}
