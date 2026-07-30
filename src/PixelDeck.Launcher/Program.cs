using System.Diagnostics;
using System.Reflection;

namespace PixelDeck.Launcher;

/// <summary>
/// PixelDeck's entry point.
/// </summary>
/// <remarks>
/// Holds only the parts that should not change from release to release: process
/// startup, logging, crash handling, installing a staged update, and loading the
/// application. Nothing about the dashboard or emulation lives here, which is
/// what lets PixelDeck.exe stay byte-for-byte identical across releases that
/// only change those.
/// </remarks>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var started = Stopwatch.StartNew();

        // Registered before anything else so a failure during startup itself is
        // still recorded rather than vanishing into a silent process exit.
        AppDomain.CurrentDomain.UnhandledException += static (_, eventArgs) =>
            LauncherLog.Write($"Unhandled exception: {eventArgs.ExceptionObject}");

        var launcherVersion = Assembly.GetExecutingAssembly().GetName().Version;
        LauncherLog.Write($"--- PixelDeck launcher {launcherVersion} starting ---");

        try
        {
            var applier = new UpdateApplier(LauncherPaths.InstallFolder, LauncherLog.Write);

            // Sweep up the pre-launcher layout and any retired executable before
            // deciding anything else, so a repaired install starts clean.
            applier.CleanUpPreviousLayout();

            if (PendingUpdate.Read() is { } pending)
            {
                LauncherLog.Write(
                    $"Pending update {pending.PreviousRelease} -> {pending.TargetRelease}, " +
                    $"{pending.Files.Count} file(s), launcher included: {pending.LauncherIncluded}.");

                var outcome = applier.Apply(pending);

                // Recorded before the restart so the application can report the
                // result whichever process ends up showing the dashboard.
                PendingUpdate.WriteOutcome(pending.TargetRelease, pending.PreviousRelease, outcome.Failure);
                PendingUpdate.Clear();

                if (outcome.LauncherReplaced)
                {
                    // This process is still running the previous launcher image,
                    // so the new one only takes effect on a fresh process.
                    LauncherLog.Write("Launcher was replaced; restarting into the new one.");
                    Relaunch(args);
                    return 0;
                }

                LauncherLog.Write(outcome.Applied
                    ? "Component-only update applied."
                    : $"Update not applied. {outcome.Failure}");
            }

            LauncherLog.Write($"Handing over to the application after {started.ElapsedMilliseconds} ms.");
            return ComponentHost.Run(args);
        }
        catch (Exception exception)
        {
            LauncherLog.Write("Startup failed.", exception);
            throw;
        }
        finally
        {
            LauncherLog.Write($"--- session ended after {started.Elapsed.TotalSeconds:0.0}s ---");
        }
    }

    private static void Relaunch(string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(LauncherPaths.LauncherExecutable)
            {
                WorkingDirectory = LauncherPaths.InstallFolder,
                UseShellExecute = false
            };

            foreach (var argument in args)
            {
                start.ArgumentList.Add(argument);
            }

            Process.Start(start);
        }
        catch (Exception exception)
        {
            LauncherLog.Write("Could not restart after replacing the launcher.", exception);
        }
    }
}
