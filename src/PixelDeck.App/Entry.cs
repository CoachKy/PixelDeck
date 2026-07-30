using Avalonia;
using System;
using PixelDeck.App.Services.Updates;

namespace PixelDeck.App;

/// <summary>
/// The contract between PixelDeck.exe and this component.
/// </summary>
/// <remarks>
/// The launcher finds <see cref="Run"/> by name after loading this assembly from
/// disk, so the type name, method name and signature are part of the release
/// format: renaming any of them makes older launchers refuse to start this
/// component, which they report as an incompatible version rather than crashing.
///
/// The STA requirement is satisfied by the launcher's entry point; this runs on
/// the thread it was called on.
/// </remarks>
public static class Entry
{
    public static int Run(string[] args)
    {
        // The launcher restarts PixelDeck with --updated-from <version> after
        // installing an update. Recording it here marks the restart in the
        // diagnostics even when the pending-update file was already consumed.
        if (UpdateHandoff.ReadUpdatedFromArgument(args) is { } previousVersion)
        {
            UpdateDiagnostics.Write($"Relaunched after updating from {previousVersion}.");
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Avalonia configuration. Also found by name by the visual designer, which
    /// is why it stays public and parameterless.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
