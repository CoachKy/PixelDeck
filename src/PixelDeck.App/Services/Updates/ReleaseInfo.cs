namespace PixelDeck.App.Services.Updates;

/// <summary>A published release that PixelDeck could update to.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Title,
    string Notes,
    DateTimeOffset? PublishedUtc,
    string AssetName,
    string AssetUrl,
    long AssetSize,
    string ReleaseUrl,
    string? ExpectedSha256)
{
    /// <summary>Human-readable asset size, e.g. "108 MB".</summary>
    public string AssetSizeText => AssetSize switch
    {
        <= 0 => "unknown size",
        < 1024L * 1024 => $"{AssetSize / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{AssetSize / (1024.0 * 1024):0.#} MB",
        _ => $"{AssetSize / (1024.0 * 1024 * 1024):0.##} GB"
    };

    public string PublishedText => PublishedUtc is { } published
        ? published.ToLocalTime().ToString("d MMM yyyy")
        : string.Empty;
}

/// <summary>Outcome of asking GitHub whether a newer release exists.</summary>
public enum UpdateCheckStatus
{
    /// <summary>The installed version is current.</summary>
    UpToDate,

    /// <summary>A newer release is published and downloadable.</summary>
    UpdateAvailable,

    /// <summary>The check could not be completed. Details go to diagnostics.</summary>
    Unavailable
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release)
{
    public static UpdateCheckResult UpToDate { get; } = new(UpdateCheckStatus.UpToDate, null);

    public static UpdateCheckResult Unavailable { get; } = new(UpdateCheckStatus.Unavailable, null);

    public static UpdateCheckResult Available(ReleaseInfo release) =>
        new(UpdateCheckStatus.UpdateAvailable, release);
}

/// <summary>Progress of downloading and preparing an update package.</summary>
/// <param name="BytesReceived">Bytes written so far.</param>
/// <param name="TotalBytes">Total expected, or null when the server did not say.</param>
public readonly record struct UpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes is > 0
        ? Math.Clamp(BytesReceived / (double)TotalBytes.Value, 0, 1)
        : null;
}

/// <summary>Where a verified, extracted update is waiting.</summary>
public sealed record StagedUpdate(ReleaseInfo Release, string StagingFolder, string ExecutablePath);
