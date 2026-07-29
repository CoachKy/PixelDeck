using PixelDeck.App.Services.Updates;

namespace PixelDeck.App.Services.Startup;

/// <summary>What the player chose when offered an update.</summary>
public enum UpdateDecision
{
    ContinueWithoutUpdating,
    UpdateNow
}

/// <summary>How startup finished, and therefore what the window should do.</summary>
public enum StartupOutcome
{
    /// <summary>Normal completion — open the dashboard.</summary>
    OpenDashboard,

    /// <summary>An update was staged. Hand over to the installer; do not open the dashboard.</summary>
    UpdateStaged
}

public sealed record StartupResult(StartupOutcome Outcome, StagedUpdate? StagedUpdate = null);

/// <summary>
/// Runs the splash screen's startup sequence, free of any UI type so it can be
/// exercised directly in tests.
/// </summary>
/// <remarks>
/// The ordering that matters: the previous update result is consumed first so a
/// half-finished update is never followed by a fresh offer, then the library
/// scan and the release check run concurrently because neither needs the other.
/// The dashboard is not reported ready until the update question is settled.
/// </remarks>
/// <param name="readPreviousUpdate">
/// Consumes whatever the previous run left behind. Injected so startup can be
/// exercised without touching the real state file.
/// </param>
/// <param name="checkForUpdatesOnStartup">
/// Passed in rather than read from settings here, so startup stays free of
/// ambient state and the behaviour is directly testable.
/// </param>
public sealed class StartupCoordinator(
    IUpdateService updateService,
    StartupPipeline pipeline,
    Version runningVersion,
    Func<Version, PreviousUpdateResult>? readPreviousUpdate = null,
    bool checkForUpdatesOnStartup = true)
{
    private readonly Func<Version, PreviousUpdateResult> _readPreviousUpdate =
        readPreviousUpdate ?? (version => UpdateStateStore.Consume(version));

    /// <summary>Runs startup. <paramref name="promptAsync"/> is only called when an update is available.</summary>
    /// <param name="loadSettingsAsync">Local configuration; must be quick.</param>
    /// <param name="initializeLibraryAsync">ROM-library initialization. Runs concurrently with the update check.</param>
    /// <param name="promptAsync">Presents the update and returns the player's choice.</param>
    /// <param name="downloadProgress">Receives byte counts while a package downloads.</param>
    public async Task<StartupResult> RunAsync(
        Func<CancellationToken, Task> loadSettingsAsync,
        Func<CancellationToken, Task> initializeLibraryAsync,
        Func<ReleaseInfo, CancellationToken, Task<UpdateDecision>> promptAsync,
        IProgress<UpdateDownloadProgress> downloadProgress,
        CancellationToken cancellationToken)
    {
        pipeline.Report("Starting PixelDeck...", 0);

        // Settle the last attempt before considering a new one.
        var previous = _readPreviousUpdate(runningVersion);
        pipeline.Report(DescribePreviousResult(previous), StartupStage.PreviousUpdateResult);

        pipeline.Report("Loading settings...", StartupStage.Settings);
        await loadSettingsAsync(cancellationToken).ConfigureAwait(false);

        // The library scan and the release check are independent. Start both,
        // then await them separately so neither gates the other's progress.
        var libraryTask = RunLibraryAsync(initializeLibraryAsync, cancellationToken);
        var updateTask = RunUpdateCheckAsync(previous, cancellationToken);

        await libraryTask.ConfigureAwait(false);
        pipeline.Report("Checking for updates...", StartupStage.Services);

        var check = await updateTask.ConfigureAwait(false);
        pipeline.Report(DescribeCheck(check), StartupStage.UpdateCheck);

        if (check.Status == UpdateCheckStatus.UpdateAvailable && check.Release is { } release)
        {
            var decision = await promptAsync(release, cancellationToken).ConfigureAwait(false);
            if (decision == UpdateDecision.UpdateNow)
            {
                // The dashboard must not come up behind an approved update.
                var staged = await PrepareAsync(release, downloadProgress, cancellationToken)
                    .ConfigureAwait(false);
                return new StartupResult(StartupOutcome.UpdateStaged, staged);
            }
        }

        pipeline.Report("Preparing dashboard...", StartupStage.Dashboard);
        pipeline.Report("Ready", StartupStage.Complete);
        return new StartupResult(StartupOutcome.OpenDashboard);
    }

    /// <summary>
    /// Library failures are reported but never abort startup, and are never
    /// conflated with update failures — the two are separate concerns.
    /// </summary>
    private async Task RunLibraryAsync(
        Func<CancellationToken, Task> initializeLibraryAsync,
        CancellationToken cancellationToken)
    {
        pipeline.Report("Checking game library...", StartupStage.Settings);
        try
        {
            await initializeLibraryAsync(cancellationToken).ConfigureAwait(false);
            pipeline.Report("Game library ready", StartupStage.RomLibrary);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            UpdateDiagnostics.Write("ROM library initialization failed.", exception);
            pipeline.Report("Game library unavailable", StartupStage.RomLibrary);
        }
    }

    /// <summary>
    /// A network problem here must not hold up startup, so the check can only
    /// ever resolve to one of the three statuses.
    /// </summary>
    private async Task<UpdateCheckResult> RunUpdateCheckAsync(
        PreviousUpdateResult previous,
        CancellationToken cancellationToken)
    {
        // Never offer a new update while the last one is unresolved.
        if (previous.Outcome is PreviousUpdateOutcome.DidNotApply or PreviousUpdateOutcome.Failed)
        {
            return UpdateCheckResult.UpToDate;
        }

        if (!checkForUpdatesOnStartup)
        {
            return UpdateCheckResult.UpToDate;
        }

        try
        {
            return await updateService.CheckAsync(runningVersion, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            UpdateDiagnostics.Write("Update check failed.", exception);
            return UpdateCheckResult.Unavailable;
        }
    }

    private async Task<StagedUpdate?> PrepareAsync(
        ReleaseInfo release,
        IProgress<UpdateDownloadProgress> downloadProgress,
        CancellationToken cancellationToken)
    {
        pipeline.Report($"Downloading PixelDeck {release.Version}...", StartupStage.UpdateCheck);
        return await updateService
            .DownloadAndStageAsync(release, downloadProgress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string DescribePreviousResult(PreviousUpdateResult previous) => previous.Outcome switch
    {
        PreviousUpdateOutcome.Succeeded => $"PixelDeck updated successfully to {previous.TargetVersion}",
        PreviousUpdateOutcome.Failed => "The last update did not finish; the previous version was restored.",
        PreviousUpdateOutcome.DidNotApply => "The last update did not complete.",
        _ => "Starting PixelDeck..."
    };

    private static string DescribeCheck(UpdateCheckResult check) => check.Status switch
    {
        UpdateCheckStatus.UpToDate => "PixelDeck is up to date.",
        UpdateCheckStatus.UpdateAvailable => $"Update {check.Release!.Version} is available.",
        _ => "Update check unavailable"
    };
}
