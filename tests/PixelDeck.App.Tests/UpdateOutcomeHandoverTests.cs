using PixelDeck.App.Services.Updates;
using LauncherPending = PixelDeck.Launcher.PendingUpdate;

namespace PixelDeck.App.Tests;

/// <summary>
/// The launcher writes how an install turned out; the application reads it and
/// tells the player. These are separate assemblies agreeing on a file format, so
/// nothing but a test that crosses the boundary can catch them drifting apart.
/// </summary>
/// <remarks>
/// This gap was real: moving installation into the launcher left the write side
/// unimplemented, and every message about a completed or failed update silently
/// stopped appearing while the tests on either side kept passing.
/// </remarks>
public sealed class UpdateOutcomeHandoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pd-outcome-{Guid.NewGuid():N}");

    private string StatePath => Path.Combine(_root, "pending-update.json");

    [Fact]
    public void SuccessfulInstall_IsReportedAsSucceededToTheVersionThatRan()
    {
        LauncherPending.WriteOutcome("1.22.073", "1.22.072", failure: null, StatePath);

        var previous = UpdateStateStore.Consume(new Version(1, 22, 73), StatePath);

        Assert.Equal(PreviousUpdateOutcome.Succeeded, previous.Outcome);
        Assert.Equal("1.22.073", previous.TargetVersion);
    }

    [Fact]
    public void RolledBackInstall_IsReportedAsFailed()
    {
        LauncherPending.WriteOutcome(
            "1.22.073",
            "1.22.072",
            "The update could not be installed and the previous version was restored.",
            StatePath);

        var previous = UpdateStateStore.Consume(new Version(1, 22, 72), StatePath);

        Assert.Equal(PreviousUpdateOutcome.Failed, previous.Outcome);
    }

    [Fact]
    public void InstallThatLeftAnOlderBuildRunning_IsReportedAsNotApplied()
    {
        // No failure was recorded, yet the build that came up is not the one the
        // update aimed at - which is exactly the loop the published 1.22.071 was
        // stuck in, and worth telling the player about rather than retrying
        // silently.
        LauncherPending.WriteOutcome("1.22.073", "1.22.072", failure: null, StatePath);

        var previous = UpdateStateStore.Consume(new Version(1, 22, 72), StatePath);

        Assert.Equal(PreviousUpdateOutcome.DidNotApply, previous.Outcome);
    }

    [Fact]
    public void ConsumingTheOutcomeRemovesIt()
    {
        LauncherPending.WriteOutcome("1.22.073", "1.22.072", failure: null, StatePath);

        UpdateStateStore.Consume(new Version(1, 22, 73), StatePath);

        // Otherwise the same message reappears on every subsequent launch.
        Assert.False(File.Exists(StatePath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
