using System.Text.Json;
using PixelDeck.App.Services.Startup;
using PixelDeck.App.Services.Updates;

namespace PixelDeck.App.Tests;

/// <summary>
/// Startup and update behaviour, driven through <see cref="StartupCoordinator"/>
/// so the whole sequence is covered without constructing any UI.
/// </summary>
public sealed class StartupUpdateTests
{
    private static readonly Version Running = new(1, 20, 70);

    private static ReleaseInfo NewerRelease(long size = 108L * 1024 * 1024) => new(
        new Version(1, 20, 71),
        "PixelDeck 1.20.071",
        "Fixed the thing.",
        DateTimeOffset.UtcNow,
        "PixelDeck-win-x64-1.20.071.zip",
        "https://example.invalid/package.zip",
        size,
        "https://example.invalid/release",
        ExpectedSha256: null);

    /// <summary>
    /// Reports straight to the collector. <see cref="Progress{T}"/> posts its
    /// callbacks asynchronously, which would let assertions run before the
    /// reports arrive.
    /// </summary>
    private sealed class SyncProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }

    private static (StartupCoordinator Coordinator, List<StartupProgress> Reports) Build(
        IUpdateService service,
        PreviousUpdateResult? previous = null,
        bool checkForUpdates = true)
    {
        var reports = new List<StartupProgress>();
        var pipeline = new StartupPipeline(new SyncProgress<StartupProgress>(reports.Add));
        return (
            new StartupCoordinator(
                service,
                pipeline,
                Running,
                _ => previous ?? PreviousUpdateResult.None,
                checkForUpdates),
            reports);
    }

    [Fact]
    public async Task UpdateCheckDisabledInSettings_SkipsTheNetworkEntirely()
    {
        var service = new StubUpdateService(UpdateCheckResult.Available(NewerRelease()))
        {
            CheckFailure = new InvalidOperationException("the check should not have run")
        };
        var (coordinator, _) = Build(service, checkForUpdates: false);

        var result = await coordinator.RunAsync(
            NoOp, NoOp, NeverPrompted, new SyncProgress<UpdateDownloadProgress>(_ => { }), CancellationToken.None);

        Assert.Equal(StartupOutcome.OpenDashboard, result.Outcome);
    }

    private static Task NoOp(CancellationToken _) => Task.CompletedTask;

    private static Task<UpdateDecision> NeverPrompted(ReleaseInfo _, CancellationToken __) =>
        throw new InvalidOperationException("The update prompt should not have been shown.");

    [Fact]
    public async Task NoUpdate_OpensDashboard()
    {
        var (coordinator, reports) = Build(new StubUpdateService(UpdateCheckResult.UpToDate));

        var result = await coordinator.RunAsync(
            NoOp, NoOp, NeverPrompted, new Progress<UpdateDownloadProgress>(), CancellationToken.None);

        Assert.Equal(StartupOutcome.OpenDashboard, result.Outcome);
        Assert.Contains(reports, report => report.Status.Contains("up to date", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NetworkFailure_StillOpensDashboard()
    {
        var service = new StubUpdateService(UpdateCheckResult.Unavailable);
        var (coordinator, reports) = Build(service);

        var result = await coordinator.RunAsync(
            NoOp, NoOp, NeverPrompted, new Progress<UpdateDownloadProgress>(), CancellationToken.None);

        Assert.Equal(StartupOutcome.OpenDashboard, result.Outcome);
        Assert.Contains(reports, report => report.Status.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CheckThatThrows_IsContainedAndOpensDashboard()
    {
        var service = new StubUpdateService(UpdateCheckResult.UpToDate)
        {
            CheckFailure = new HttpRequestException("boom")
        };
        var (coordinator, _) = Build(service);

        var result = await coordinator.RunAsync(
            NoOp, NoOp, NeverPrompted, new Progress<UpdateDownloadProgress>(), CancellationToken.None);

        Assert.Equal(StartupOutcome.OpenDashboard, result.Outcome);
    }

    [Fact]
    public async Task LibraryFailure_IsNotTreatedAsUpdateFailure()
    {
        var service = new StubUpdateService(UpdateCheckResult.UpToDate);
        var (coordinator, reports) = Build(service);

        var result = await coordinator.RunAsync(
            NoOp,
            _ => throw new IOException("library exploded"),
            NeverPrompted,
            new Progress<UpdateDownloadProgress>(),
            CancellationToken.None);

        Assert.Equal(StartupOutcome.OpenDashboard, result.Outcome);
        Assert.Contains(reports, report => report.Status.Contains("library unavailable", StringComparison.OrdinalIgnoreCase));
        // The update check still ran and reported its own, separate result.
        Assert.Contains(reports, report => report.Status.Contains("up to date", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LibraryAndUpdateCheck_RunConcurrently()
    {
        var libraryStarted = new TaskCompletionSource();
        var checkStarted = new TaskCompletionSource();

        // Each task refuses to finish until the other has started, so this can
        // only complete if they genuinely overlap.
        var service = new StubUpdateService(UpdateCheckResult.UpToDate)
        {
            OnCheck = async () =>
            {
                checkStarted.TrySetResult();
                await libraryStarted.Task;
            }
        };
        var (coordinator, _) = Build(service);

        var run = coordinator.RunAsync(
            NoOp,
            async _ =>
            {
                libraryStarted.TrySetResult();
                await checkStarted.Task;
            },
            NeverPrompted,
            new Progress<UpdateDownloadProgress>(),
            CancellationToken.None);

        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(run, finished);
        Assert.Equal(StartupOutcome.OpenDashboard, (await run).Outcome);
    }

    [Fact]
    public async Task UpdateAvailable_ContinueWithoutUpdating_OpensDashboard()
    {
        var service = new StubUpdateService(UpdateCheckResult.Available(NewerRelease()));
        var (coordinator, reports) = Build(service);

        var result = await coordinator.RunAsync(
            NoOp,
            NoOp,
            (_, _) => Task.FromResult(UpdateDecision.ContinueWithoutUpdating),
            new Progress<UpdateDownloadProgress>(),
            CancellationToken.None);

        Assert.Equal(StartupOutcome.OpenDashboard, result.Outcome);
        Assert.False(service.DownloadCalled);
        Assert.Contains(reports, report => report.Status.Contains("1.20.71", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateNow_StagesAndDoesNotOpenDashboard()
    {
        var release = NewerRelease();
        var service = new StubUpdateService(UpdateCheckResult.Available(release))
        {
            Staged = new StagedUpdate(release, @"C:\staging", @"C:\staging\PixelDeck.App.exe")
        };
        var (coordinator, _) = Build(service);

        var result = await coordinator.RunAsync(
            NoOp,
            NoOp,
            (_, _) => Task.FromResult(UpdateDecision.UpdateNow),
            new Progress<UpdateDownloadProgress>(),
            CancellationToken.None);

        Assert.Equal(StartupOutcome.UpdateStaged, result.Outcome);
        Assert.NotNull(result.StagedUpdate);
        Assert.True(service.DownloadCalled);
    }

    [Fact]
    public async Task UpdateNow_ReportsDownloadProgress()
    {
        var release = NewerRelease(size: 1000);
        var seen = new List<UpdateDownloadProgress>();
        var service = new StubUpdateService(UpdateCheckResult.Available(release))
        {
            Staged = new StagedUpdate(release, "staging", "staging/PixelDeck.App.exe"),
            OnDownload = progress =>
            {
                progress.Report(new UpdateDownloadProgress(250, 1000));
                progress.Report(new UpdateDownloadProgress(1000, 1000));
            }
        };
        var (coordinator, _) = Build(service);

        await coordinator.RunAsync(
            NoOp,
            NoOp,
            (_, _) => Task.FromResult(UpdateDecision.UpdateNow),
            new SyncProgress<UpdateDownloadProgress>(seen.Add),
            CancellationToken.None);

        Assert.Equal(2, seen.Count);
        Assert.Equal(0.25, seen[0].Fraction);
        Assert.Equal(1.0, seen[1].Fraction);
    }

    [Fact]
    public async Task CancelledDownload_PropagatesSoStartupCanResume()
    {
        var release = NewerRelease();
        var service = new StubUpdateService(UpdateCheckResult.Available(release))
        {
            DownloadFailure = new OperationCanceledException()
        };
        var (coordinator, _) = Build(service);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RunAsync(
            NoOp,
            NoOp,
            (_, _) => Task.FromResult(UpdateDecision.UpdateNow),
            new Progress<UpdateDownloadProgress>(),
            CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedPreparation_SurfacesRetryability(bool retryable)
    {
        var release = NewerRelease();
        var service = new StubUpdateService(UpdateCheckResult.Available(release))
        {
            DownloadFailure = new UpdatePreparationException("The update could not be prepared.", retryable)
        };
        var (coordinator, _) = Build(service);

        var failure = await Assert.ThrowsAsync<UpdatePreparationException>(() => coordinator.RunAsync(
            NoOp,
            NoOp,
            (_, _) => Task.FromResult(UpdateDecision.UpdateNow),
            new Progress<UpdateDownloadProgress>(),
            CancellationToken.None));

        Assert.Equal(retryable, failure.IsRetryable);
        // The message is short and safe to put on the splash.
        Assert.DoesNotContain("Exception", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartupProgress_NeverMovesBackwards()
    {
        var reports = new List<StartupProgress>();
        var pipeline = new StartupPipeline(new SyncProgress<StartupProgress>(reports.Add));

        pipeline.Report("a", 40);
        pipeline.Report("b", 10);
        pipeline.Report("c", 55);

        Assert.Equal([40, 40, 55], reports.Select(report => report.Percent));
    }

    [Theory]
    [InlineData(PreviousUpdateOutcome.DidNotApply)]
    [InlineData(PreviousUpdateOutcome.Failed)]
    public async Task UnresolvedPreviousUpdate_SuppressesANewOffer(PreviousUpdateOutcome outcome)
    {
        // An update is available, but the last attempt never confirmed, so the
        // player must not be offered another one. NeverPrompted throws if the
        // prompt is shown.
        var service = new StubUpdateService(UpdateCheckResult.Available(NewerRelease()));
        var (coordinator, _) = Build(service, new PreviousUpdateResult(outcome, "1.20.071"));

        var result = await coordinator.RunAsync(
            NoOp, NoOp, NeverPrompted, new SyncProgress<UpdateDownloadProgress>(_ => { }), CancellationToken.None);

        Assert.Equal(StartupOutcome.OpenDashboard, result.Outcome);
        Assert.False(service.DownloadCalled);
    }

    [Fact]
    public async Task ConfirmedPreviousUpdate_IsReportedAndAllowsANewOffer()
    {
        var service = new StubUpdateService(UpdateCheckResult.Available(NewerRelease()));
        var (coordinator, reports) = Build(
            service,
            new PreviousUpdateResult(PreviousUpdateOutcome.Succeeded, "1.20.070"));

        var result = await coordinator.RunAsync(
            NoOp,
            NoOp,
            (_, _) => Task.FromResult(UpdateDecision.ContinueWithoutUpdating),
            new SyncProgress<UpdateDownloadProgress>(_ => { }),
            CancellationToken.None);

        Assert.Equal(StartupOutcome.OpenDashboard, result.Outcome);
        Assert.Contains(reports, report =>
            report.Status.Contains("updated successfully", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PreviousUpdate_ConfirmsWhenRunningVersionMatchesTarget()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"pd-update-{Guid.NewGuid():N}.json");
        UpdateStateStore.Write(new PendingUpdateState { TargetVersion = "1.20.70" }, statePath);

        var previous = UpdateStateStore.Consume(Running, statePath);

        Assert.Equal(PreviousUpdateOutcome.Succeeded, previous.Outcome);
    }

    [Fact]
    public void PreviousUpdate_ReportsRollback()
    {
        var statePath = Path.Combine(Path.GetTempPath(), $"pd-update-{Guid.NewGuid():N}.json");
        UpdateStateStore.Write(
            new PendingUpdateState { TargetVersion = "1.20.071", Failure = "copy failed" },
            statePath);

        var previous = UpdateStateStore.Consume(Running, statePath);

        Assert.Equal(PreviousUpdateOutcome.Failed, previous.Outcome);
    }

    [Fact]
    public void ReleaseParsing_PicksZipAssetAndChecksum()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "tag_name": "v1.20.071",
              "name": "PixelDeck 1.20.071",
              "body": "# Heading\nFixed Star Ocean.\nAdded library images.",
              "html_url": "https://example.invalid/r",
              "published_at": "2026-07-28T10:00:00Z",
              "draft": false,
              "prerelease": false,
              "assets": [
                { "name": "PixelDeck-win-x64-1.20.071.zip", "browser_download_url": "https://example.invalid/p.zip", "size": 1234 },
                { "name": "PixelDeck-win-x64-1.20.071.zip.sha256", "browser_download_url": "https://example.invalid/p.sha256", "size": 64 }
              ]
            }
            """);

        var release = GitHubUpdateService.ParseRelease(document.RootElement);

        Assert.NotNull(release);
        Assert.Equal(new Version(1, 20, 71), release!.Version);
        Assert.Equal("PixelDeck-win-x64-1.20.071.zip", release.AssetName);
        Assert.Equal("https://example.invalid/p.sha256", release.ExpectedSha256);
        Assert.Equal(1234, release.AssetSize);
        // Markdown headings are dropped from the splash summary.
        Assert.DoesNotContain("#", release.Notes, StringComparison.Ordinal);
        Assert.Contains("Star Ocean", release.Notes, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"tag_name\":\"v1.0.0\",\"draft\":true,\"assets\":[]}")]
    [InlineData("{\"tag_name\":\"v1.0.0\",\"prerelease\":true,\"assets\":[]}")]
    [InlineData("{\"tag_name\":\"not-a-version\",\"assets\":[]}")]
    [InlineData("{\"tag_name\":\"v1.0.0\",\"assets\":[{\"name\":\"notes.txt\",\"browser_download_url\":\"u\"}]}")]
    public void ReleaseParsing_RejectsUnusableReleases(string json)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Null(GitHubUpdateService.ParseRelease(document.RootElement));
    }

    [Fact]
    public void AssetSizeText_IsHumanReadable()
    {
        Assert.Equal("108 MB", NewerRelease(108L * 1024 * 1024).AssetSizeText);
        Assert.Equal("unknown size", NewerRelease(0).AssetSizeText);
    }

    private sealed class StubUpdateService(UpdateCheckResult result) : IUpdateService
    {
        public Exception? CheckFailure { get; init; }

        public Exception? DownloadFailure { get; init; }

        public StagedUpdate? Staged { get; init; }

        public Func<Task>? OnCheck { get; init; }

        public Action<IProgress<UpdateDownloadProgress>>? OnDownload { get; init; }

        public bool DownloadCalled { get; private set; }

        public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken)
        {
            if (OnCheck is not null)
            {
                await OnCheck().ConfigureAwait(false);
            }

            return CheckFailure is not null ? throw CheckFailure : result;
        }

        public Task<StagedUpdate> DownloadAndStageAsync(
            ReleaseInfo release,
            IProgress<UpdateDownloadProgress> progress,
            CancellationToken cancellationToken)
        {
            DownloadCalled = true;
            OnDownload?.Invoke(progress);
            return DownloadFailure is not null
                ? Task.FromException<StagedUpdate>(DownloadFailure)
                : Task.FromResult(Staged!);
        }
    }
}
