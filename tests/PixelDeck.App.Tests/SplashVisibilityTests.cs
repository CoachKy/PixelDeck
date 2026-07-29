using PixelDeck.App.Services.Startup;
using PixelDeck.App.Services.Updates;
using PixelDeck.App.ViewModels;

namespace PixelDeck.App.Tests;

/// <summary>
/// The loading splash belongs to startup. Library rescans happen throughout a
/// session — the folder watcher fires one every time a game exits and writes
/// its save and play history — and must not raise it again.
/// </summary>
public sealed class SplashVisibilityTests
{
    private sealed class NoUpdateService : IUpdateService
    {
        public Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken) =>
            Task.FromResult(UpdateCheckResult.UpToDate);

        public Task<StagedUpdate> DownloadAndStageAsync(
            ReleaseInfo release,
            IProgress<UpdateDownloadProgress> progress,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no update should be offered");
    }

    [Fact]
    public void Splash_IsUpBeforeStartupRuns()
    {
        // It has to cover the window from the first rendered frame, so the flag
        // starts set rather than waiting for the first scan to begin.
        using var viewModel = new MainViewModel();

        Assert.True(viewModel.IsStartingUp);
    }

    [Fact]
    public async Task Splash_ClosesWhenStartupFinishes()
    {
        using var viewModel = new MainViewModel();

        var result = await viewModel.RunStartupAsync(new NoUpdateService(), CancellationToken.None);

        Assert.Equal(StartupOutcome.OpenDashboard, result.Outcome);
        Assert.False(viewModel.IsStartingUp);
    }

    [Fact]
    public async Task Splash_StaysDownWhenTheLibraryRescans()
    {
        using var viewModel = new MainViewModel();
        await viewModel.RunStartupAsync(new NoUpdateService(), CancellationToken.None);
        Assert.False(viewModel.IsStartingUp);

        // Exactly what quitting a game triggers, by way of the folder watcher.
        await viewModel.RefreshAsync();

        Assert.False(viewModel.IsStartingUp);
    }

    [Fact]
    public async Task Rescan_StillMarksTheViewModelBusy()
    {
        // IsBusy keeps its own meaning: a scan is running, so the "no games"
        // message must stay hidden. Splitting the splash off must not change it.
        using var viewModel = new MainViewModel();
        await viewModel.RunStartupAsync(new NoUpdateService(), CancellationToken.None);

        var busyDuringScan = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsBusy) && viewModel.IsBusy)
            {
                busyDuringScan = true;
            }
        };

        await viewModel.RefreshAsync();

        Assert.True(busyDuringScan);
        Assert.False(viewModel.IsBusy);
    }
}
