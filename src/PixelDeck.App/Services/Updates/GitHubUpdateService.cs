using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace PixelDeck.App.Services.Updates;

/// <summary>
/// Checks the project's GitHub releases and prepares an update package.
/// </summary>
/// <remarks>
/// Releases publish a self-contained zip whose root holds PixelDeck.App.exe.
/// A release may also carry a <c>.sha256</c> sidecar asset for the package; when
/// present it is enforced, because GitHub does not checksum assets itself.
/// </remarks>
public sealed class GitHubUpdateService : IUpdateService, IDisposable
{
    private const string ReleasesUrl = "https://api.github.com/repos/CoachKy/PixelDeck/releases/latest";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _client;
    private readonly string _downloadFolder;
    private readonly string _stagingFolder;
    private readonly UpdatePlatform _platform;

    public GitHubUpdateService(
        HttpClient? client = null,
        string? updateRoot = null,
        UpdatePlatform? platform = null)
    {
        _platform = platform ?? UpdatePlatform.Current;
        _client = client ?? new HttpClient();
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("PixelDeck-Updater");
        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var root = updateRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PixelDeck",
            "updates");
        _downloadFolder = Path.Combine(root, "download");
        _stagingFolder = Path.Combine(root, "staging");
    }

    public async Task<UpdateCheckResult> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken)
    {
        try
        {
            // The check gets its own deadline so a hanging network cannot hold
            // the splash on "Checking for updates" indefinitely.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(CheckTimeout);

            using var response = await _client
                .GetAsync(ReleasesUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                UpdateDiagnostics.Write($"Release check returned {(int)response.StatusCode} {response.ReasonPhrase}.");
                return UpdateCheckResult.Unavailable;
            }

            await using var payload = await response.Content
                .ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var document = await JsonDocument
                .ParseAsync(payload, cancellationToken: deadline.Token).ConfigureAwait(false);

            var release = ParseRelease(document.RootElement, _platform);
            if (release is null)
            {
                UpdateDiagnostics.Write(
                    $"Latest release has no package for {_platform.RuntimeIdentifier}.");
                return UpdateCheckResult.Unavailable;
            }

            if (release.Version <= currentVersion)
            {
                return UpdateCheckResult.UpToDate;
            }

            UpdateDiagnostics.Write($"Update available: {currentVersion} -> {release.Version} ({release.AssetName}).");
            return UpdateCheckResult.Available(release);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            UpdateDiagnostics.Write("Release check timed out.");
            return UpdateCheckResult.Unavailable;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException)
        {
            UpdateDiagnostics.Write("Release check failed.", exception);
            return UpdateCheckResult.Unavailable;
        }
    }

    /// <summary>
    /// Reads the fields PixelDeck needs out of a GitHub release payload, or
    /// returns null when the release carries nothing installable.
    /// </summary>
    internal static ReleaseInfo? ParseRelease(JsonElement root) =>
        ParseRelease(root, UpdatePlatform.Current);

    internal static ReleaseInfo? ParseRelease(JsonElement root, UpdatePlatform platform)
    {
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
        {
            return null;
        }

        if (root.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
        {
            return null;
        }

        var tag = root.TryGetProperty("tag_name", out var tagName) ? tagName.GetString() : null;
        if (!TryParseVersion(tag, out var version))
        {
            return null;
        }

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // Pick the package built for this machine. A Raspberry Pi must never be
        // offered the Windows build, so the runtime identifier has to match.
        string? assetName = null, assetUrl = null;
        long assetSize = 0;
        var bestRank = int.MaxValue;
        var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name is null || url is null)
            {
                continue;
            }

            if (name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            {
                checksums[name[..^".sha256".Length]] = url;
                continue;
            }

            if (!platform.Matches(name))
            {
                continue;
            }

            var rank = platform.Rank(name);
            if (rank >= bestRank)
            {
                continue;
            }

            bestRank = rank;
            assetName = name;
            assetUrl = url;
            assetSize = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0;
        }

        if (assetName is null || assetUrl is null)
        {
            return null;
        }

        var checksumUrl = checksums.GetValueOrDefault(assetName);

        var title = root.TryGetProperty("name", out var titleElement)
            ? titleElement.GetString() ?? $"PixelDeck {version}"
            : $"PixelDeck {version}";
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty;
        var releaseUrl = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? string.Empty : string.Empty;
        DateTimeOffset? published = root.TryGetProperty("published_at", out var publishedAt) &&
                                    publishedAt.TryGetDateTimeOffset(out var value)
            ? value
            : null;

        return new ReleaseInfo(
            version,
            title,
            Summarize(notes),
            published,
            assetName,
            assetUrl,
            assetSize,
            releaseUrl,
            checksumUrl);
    }

    /// <summary>Accepts tags like "v1.20.071" or "1.20.71".</summary>
    internal static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var trimmed = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(trimmed, out version!);
    }

    /// <summary>
    /// Condenses release notes to the first few meaningful lines. The splash has
    /// room for a summary, not a changelog.
    /// </summary>
    internal static string Summarize(string notes, int maximumLines = 4, int maximumLength = 320)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return string.Empty;
        }

        var lines = notes
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Take(maximumLines);

        var summary = string.Join(Environment.NewLine, lines);
        return summary.Length <= maximumLength ? summary : summary[..maximumLength].TrimEnd() + "...";
    }

    public async Task<StagedUpdate> DownloadAndStageAsync(
        ReleaseInfo release,
        IProgress<UpdateDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_downloadFolder);
        var packagePath = Path.Combine(_downloadFolder, release.AssetName);

        try
        {
            await DownloadPackageAsync(release, packagePath, progress, cancellationToken).ConfigureAwait(false);
            await VerifyPackageAsync(release, packagePath, cancellationToken).ConfigureAwait(false);
            return ExtractPackage(release, packagePath);
        }
        catch (OperationCanceledException)
        {
            // A cancelled update must not leave a half-written package behind.
            CleanUp(packagePath);
            throw;
        }
        catch (UpdatePreparationException)
        {
            CleanUp(packagePath);
            throw;
        }
        catch (Exception exception)
        {
            UpdateDiagnostics.Write("Update preparation failed.", exception);
            CleanUp(packagePath);
            throw new UpdatePreparationException("The update could not be prepared.", isRetryable: true);
        }
    }

    private async Task DownloadPackageAsync(
        ReleaseInfo release,
        string packagePath,
        IProgress<UpdateDownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        using var response = await _client
            .GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            UpdateDiagnostics.Write($"Download returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            throw new UpdatePreparationException("The update could not be downloaded.", isRetryable: true);
        }

        var total = response.Content.Headers.ContentLength ?? (release.AssetSize > 0 ? release.AssetSize : null);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            packagePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            progress.Report(new UpdateDownloadProgress(received, total));
        }

        if (total is > 0 && received != total)
        {
            UpdateDiagnostics.Write($"Download size mismatch: expected {total}, received {received}.");
            throw new UpdatePreparationException("The update download was incomplete.", isRetryable: true);
        }
    }

    private async Task VerifyPackageAsync(
        ReleaseInfo release,
        string packagePath,
        CancellationToken cancellationToken)
    {
        if (release.ExpectedSha256 is null)
        {
            UpdateDiagnostics.Write("Release publishes no checksum asset; skipping SHA-256 verification.");
            return;
        }

        string expected;
        try
        {
            var text = await _client.GetStringAsync(release.ExpectedSha256, cancellationToken).ConfigureAwait(false);
            // Sidecars are usually "<hash>  <filename>".
            expected = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        }
        catch (Exception exception) when (exception is HttpRequestException or IndexOutOfRangeException)
        {
            UpdateDiagnostics.Write("Checksum asset could not be read.", exception);
            throw new UpdatePreparationException("The update could not be verified.", isRetryable: true);
        }

        var actual = await ComputeSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            UpdateDiagnostics.Write($"Checksum mismatch. Expected {expected}, computed {actual}.");
            throw new UpdatePreparationException(
                "The update failed its security check and was discarded.",
                isRetryable: false);
        }
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private StagedUpdate ExtractPackage(ReleaseInfo release, string packagePath)
    {
        if (Directory.Exists(_stagingFolder))
        {
            Directory.Delete(_stagingFolder, recursive: true);
        }

        Directory.CreateDirectory(_stagingFolder);

        if (packagePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            ExtractTarball(packagePath);
        }
        else
        {
            ExtractZip(packagePath);
        }

        var executable = Path.Combine(_stagingFolder, _platform.ExecutableName);
        if (!File.Exists(executable))
        {
            UpdateDiagnostics.Write($"Staged package has no {_platform.ExecutableName} at its root.");
            throw new UpdatePreparationException(
                "The update package was not valid and was discarded.",
                isRetryable: false);
        }

        // Zip carries no Unix permissions, and a tarball's may not survive every
        // toolchain, so the launcher bit is set explicitly either way.
        _platform.EnsureExecutable(executable);

        UpdateDiagnostics.Write(
            $"Staged {release.Version} for {_platform.RuntimeIdentifier} at {_stagingFolder}.");
        return new StagedUpdate(release, _stagingFolder, executable);
    }

    private void ExtractZip(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            // Directory markers have no name.
            if (entry.Name.Length == 0)
            {
                continue;
            }

            var destination = ResolveInsideStaging(entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>Extracts a gzip-compressed tarball, the Linux release format.</summary>
    private void ExtractTarball(string packagePath)
    {
        using var file = File.OpenRead(packagePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);

        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }

            var destination = ResolveInsideStaging(entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>
    /// Resolves an archive entry beneath the staging folder, rejecting paths
    /// that try to escape it. Archive contents are untrusted input.
    /// </summary>
    private string ResolveInsideStaging(string entryPath)
    {
        var root = Path.GetFullPath(_stagingFolder) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(root, entryPath));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            UpdateDiagnostics.Write($"Rejected archive entry outside staging: {entryPath}");
            throw new UpdatePreparationException(
                "The update package was not valid and was discarded.",
                isRetryable: false);
        }

        return candidate;
    }

    private static void CleanUp(string packagePath)
    {
        try
        {
            if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            UpdateDiagnostics.Write("Could not remove partial download.", exception);
        }
    }

    public void Dispose() => _client.Dispose();
}
