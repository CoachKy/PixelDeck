using Avalonia;
using System;
using PixelDeck.App.Services.Updates;

namespace PixelDeck.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // The updater relaunches PixelDeck with --updated-from <version>.
        // Recording it here gives diagnostics a marker for the restart even if
        // the pending-update file was lost, and costs nothing when absent.
        if (UpdateHandoff.ReadUpdatedFromArgument(args) is { } previousVersion)
        {
            UpdateDiagnostics.Write($"Relaunched after updating from {previousVersion}.");
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
