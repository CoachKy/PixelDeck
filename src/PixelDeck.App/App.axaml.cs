using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using PixelDeck.App.Input;
using PixelDeck.App.Services;
using PixelDeck.App.ViewModels;
using PixelDeck.App.Views;

namespace PixelDeck.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += static (_, _) =>
            {
                GamepadManager.Shutdown();

                // Queued diagnostics are written on a background thread, so
                // without this the last lines before a quit or a crash — the
                // ones worth reading — never reach the file. PixelCube keeps
                // its own trace file and needs the same treatment.
                PixelCubeDiagnostics.Flush();
                EmulatorDiagnostics.Flush();
            };
            var directGamePath = GetDirectGamePath(desktop.Args);
            if (directGamePath is not null)
            {
                var library = new GameLibrary();
                var game = library.ScanAsync().GetAwaiter().GetResult()
                    .FirstOrDefault(entry => string.Equals(entry.FullPath, directGamePath, StringComparison.OrdinalIgnoreCase));

                desktop.MainWindow = game is not { CanLaunch: true }
                    ? new MainWindow { DataContext = new MainViewModel() }
                    : new EmulatorWindow(game);
            }
            else
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainViewModel(),
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static string? GetDirectGamePath(string[]? args)
    {
        if (args is null)
        {
            return null;
        }

        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--game", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFullPath(args[index + 1]);
            }
        }

        return null;
    }
}
