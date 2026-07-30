using System.Reflection;
using System.Text.RegularExpressions;

namespace PixelDeck.App.Services.Updates;

/// <summary>
/// Recognises the small, platform-independent component archive a release
/// publishes alongside its full packages.
/// </summary>
/// <remarks>
/// A component archive replaces only the assemblies under Components, so it is
/// valid only for the launcher it was built against. That launcher version is
/// carried in the file name rather than in a separate manifest on purpose: the
/// update check runs while the splash is up under an eight-second budget, and
/// deciding from the asset list costs no additional request.
///
/// manifest.json still ships with the release for verification and for anyone
/// inspecting it by hand; it is simply not on the startup path.
/// </remarks>
internal static partial class ComponentArchive
{
    [GeneratedRegex(
        @"^PixelDeck-v\d+\.\d+\.\d+-components-launcher(?<launcher>\d+(?:\.\d+)*)\.zip$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>
    /// The launcher running this process.
    /// </summary>
    /// <remarks>
    /// The entry assembly is PixelDeck.exe, because the launcher owns Main and
    /// loads this component. Reading it this way avoids the application needing a
    /// reference to the launcher, which would defeat keeping them separable.
    /// </remarks>
    public static Version? RunningLauncherVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version;

    /// <summary>
    /// True when <paramref name="assetName"/> is a component archive, reporting
    /// which launcher version it targets.
    /// </summary>
    public static bool TryMatch(string assetName, out Version? launcherVersion)
    {
        launcherVersion = null;

        var match = Pattern().Match(assetName);
        if (!match.Success)
        {
            return false;
        }

        // A malformed version means the asset is unusable rather than fatal:
        // the caller falls back to the full package.
        return Version.TryParse(match.Groups["launcher"].Value, out launcherVersion);
    }

    /// <summary>
    /// Whether this machine can take the component-only path for an asset.
    /// </summary>
    /// <remarks>
    /// Compares only the components the launcher actually declares. A launcher
    /// reporting 1.0.0.0 and an archive built for 1.0.0 are the same launcher,
    /// and must not be treated as a mismatch that forces a 44 MB download.
    /// </remarks>
    public static bool IsUsableHere(string assetName)
    {
        if (!TryMatch(assetName, out var target) || target is null)
        {
            return false;
        }

        var running = RunningLauncherVersion;
        if (running is null)
        {
            return false;
        }

        return running.Major == target.Major
            && running.Minor == target.Minor
            && Math.Max(running.Build, 0) == Math.Max(target.Build, 0);
    }
}
