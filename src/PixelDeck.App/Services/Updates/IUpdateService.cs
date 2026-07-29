namespace PixelDeck.App.Services.Updates;

/// <summary>
/// Finds, downloads and stages PixelDeck updates. Nothing here replaces any
/// installed file — staging stops at a verified, extracted copy so the caller
/// can decide whether to hand over to an installer.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Asks whether a newer release exists. Never throws for network problems:
    /// those return <see cref="UpdateCheckStatus.Unavailable"/> and are logged.
    /// </summary>
    Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads, verifies and extracts a release into staging. Throws
    /// <see cref="UpdatePreparationException"/> with a player-safe message when
    /// preparation fails, and <see cref="OperationCanceledException"/> when
    /// cancelled.
    /// </summary>
    Task<StagedUpdate> DownloadAndStageAsync(
        ReleaseInfo release,
        IProgress<UpdateDownloadProgress> progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// A preparation failure whose <see cref="Exception.Message"/ > is safe to show
/// on the splash screen. Detail belongs in <see cref="UpdateDiagnostics"/>.
/// </summary>
public sealed class UpdatePreparationException(string message, bool isRetryable)
    : Exception(message)
{
    /// <summary>Whether offering "Retry" makes sense for this failure.</summary>
    public bool IsRetryable { get; } = isRetryable;
}
