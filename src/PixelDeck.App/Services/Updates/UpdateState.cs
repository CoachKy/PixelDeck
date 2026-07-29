using System.Text.Json;

namespace PixelDeck.App.Services.Updates;

/// <summary>What the previous run left behind about an update attempt.</summary>
public sealed class PendingUpdateState
{
    /// <summary>Version the update was meant to install.</summary>
    public string? TargetVersion { get; set; }

    /// <summary>Version that was running when the update was approved.</summary>
    public string? PreviousVersion { get; set; }

    /// <summary>Set by the installer when it could not complete and rolled back.</summary>
    public string? Failure { get; set; }
}

/// <summary>How the previous update attempt turned out.</summary>
public enum PreviousUpdateOutcome
{
    /// <summary>No update was attempted.</summary>
    None,

    /// <summary>The running version matches what the update targeted.</summary>
    Succeeded,

    /// <summary>An update was attempted but this build is not the target version.</summary>
    DidNotApply,

    /// <summary>The installer reported a failure and restored the old version.</summary>
    Failed
}

public sealed record PreviousUpdateResult(PreviousUpdateOutcome Outcome, string? TargetVersion)
{
    public static PreviousUpdateResult None { get; } = new(PreviousUpdateOutcome.None, null);
}

/// <summary>
/// Persists the one fact that has to survive a restart: which version an
/// approved update was aiming for, so the relaunched build can confirm it.
/// </summary>
public static class UpdateStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string StatePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PixelDeck",
        "pending-update.json");

    public static PendingUpdateState? Read(string? path = null)
    {
        var target = path ?? StatePath;
        try
        {
            return File.Exists(target)
                ? JsonSerializer.Deserialize<PendingUpdateState>(File.ReadAllText(target), JsonOptions)
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            UpdateDiagnostics.Write("Pending update state could not be read.", exception);
            return null;
        }
    }

    public static void Write(PendingUpdateState state, string? path = null)
    {
        var target = path ?? StatePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            UpdateDiagnostics.Write("Pending update state could not be written.", exception);
        }
    }

    public static void Clear(string? path = null)
    {
        var target = path ?? StatePath;
        try
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            UpdateDiagnostics.Write("Pending update state could not be cleared.", exception);
        }
    }

    /// <summary>
    /// Interprets whatever the previous run left, and clears it. Called before
    /// any new check so PixelDeck never offers another update while the last
    /// one is still unconfirmed.
    /// </summary>
    public static PreviousUpdateResult Consume(Version runningVersion, string? path = null)
    {
        var state = Read(path);
        if (state is null)
        {
            return PreviousUpdateResult.None;
        }

        Clear(path);

        if (!string.IsNullOrWhiteSpace(state.Failure))
        {
            UpdateDiagnostics.Write($"Previous update failed and was rolled back: {state.Failure}");
            return new PreviousUpdateResult(PreviousUpdateOutcome.Failed, state.TargetVersion);
        }

        if (GitHubUpdateService.TryParseVersion(state.TargetVersion, out var target) &&
            runningVersion >= target)
        {
            UpdateDiagnostics.Write($"Update to {target} confirmed running.");
            return new PreviousUpdateResult(PreviousUpdateOutcome.Succeeded, state.TargetVersion);
        }

        UpdateDiagnostics.Write(
            $"Update to {state.TargetVersion} did not apply; still running {runningVersion}.");
        return new PreviousUpdateResult(PreviousUpdateOutcome.DidNotApply, state.TargetVersion);
    }
}
