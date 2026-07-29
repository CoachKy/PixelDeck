namespace PixelDeck.App.Services.Startup;

/// <summary>A named startup stage and how far through startup it sits.</summary>
public readonly record struct StartupProgress(string Status, int Percent);

/// <summary>
/// Drives the splash screen's startup sequence: named stages, a percentage that
/// only ever moves forward, and independent work running concurrently.
/// </summary>
/// <remarks>
/// This is deliberately small. The startup sequence has a handful of steps with
/// real ordering constraints between only two of them, so a general task-graph
/// framework would be more machinery than the problem needs.
/// </remarks>
public sealed class StartupPipeline(IProgress<StartupProgress> progress)
{
    private int _percent;

    /// <summary>
    /// Reports a stage. Percentages never move backwards, so a fast concurrent
    /// task cannot make the bar jump back when a slower one reports later.
    /// </summary>
    public void Report(string status, int percent)
    {
        _percent = Math.Clamp(Math.Max(_percent, percent), 0, 100);
        progress.Report(new StartupProgress(status, _percent));
    }

    /// <summary>Current percentage, for callers that need to resume from it.</summary>
    public int Percent => _percent;
}

/// <summary>The startup stages, with the percentage each one completes at.</summary>
public static class StartupStage
{
    public const int PreviousUpdateResult = 10;
    public const int Settings = 20;
    public const int RomLibrary = 45;
    public const int Services = 60;
    public const int UpdateCheck = 75;
    public const int Dashboard = 90;
    public const int Complete = 100;
}
