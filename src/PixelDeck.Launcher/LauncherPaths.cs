namespace PixelDeck.Launcher;

/// <summary>
/// Every location the launcher needs, resolved once.
/// </summary>
/// <remarks>
/// <see cref="InstallFolder"/> comes from <see cref="AppContext.BaseDirectory"/>
/// rather than the assembly location, which returns an empty string in a
/// single-file build. Diagnostics and pending-update state live under
/// LocalApplicationData, outside the install folder, so an update that replaces
/// the install cannot destroy the record of what it was doing.
/// </remarks>
internal static class LauncherPaths
{
    /// <summary>The folder PixelDeck.exe sits in.</summary>
    public static string InstallFolder { get; } = AppContext.BaseDirectory.TrimEnd(
        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>Where the replaceable application assemblies live.</summary>
    public static string ComponentFolder { get; } = Path.Combine(InstallFolder, "Components");

    /// <summary>The application assembly the launcher hands control to.</summary>
    public static string ApplicationAssembly { get; } =
        Path.Combine(ComponentFolder, "PixelDeck.App.dll");

    /// <summary>This executable, as the running process sees it.</summary>
    public static string LauncherExecutable { get; } = Path.Combine(
        InstallFolder,
        OperatingSystem.IsWindows() ? "PixelDeck.exe" : "PixelDeck");

    private static string StateFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixelDeck");

    public static string TraceLog { get; } = Path.Combine(StateFolder, "launcher.log");

    /// <summary>Written by the application, read by the next launcher start.</summary>
    public static string PendingUpdate { get; } = Path.Combine(StateFolder, "pending-components.json");

    /// <summary>
    /// Where the launcher records how an install turned out, for the application
    /// to report once it starts.
    /// </summary>
    /// <remarks>
    /// The name and shape are the application's, not the launcher's: it already
    /// consumes this file to tell the player an update succeeded or was rolled
    /// back. The launcher writes it because the launcher is now what installs.
    /// </remarks>
    public static string UpdateOutcome { get; } = Path.Combine(StateFolder, "pending-update.json");

    public static void EnsureStateFolder() => Directory.CreateDirectory(StateFolder);
}
