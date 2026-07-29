using System.Text.Json;
using PixelDeck.App.Services.Updates;

namespace PixelDeck.App.Tests;

/// <summary>
/// Release-asset selection across platforms. PixelDeck ships for Windows
/// desktops and Raspberry Pi, so a build must never be offered the wrong one.
/// </summary>
public sealed class UpdatePlatformTests
{
    private static readonly UpdatePlatform Windows =
        new("win-x64", "PixelDeck.App.exe", [".zip"], RequiresExecutableBit: false);

    private static readonly UpdatePlatform RaspberryPi =
        new("linux-arm64", "PixelDeck.App", [".tar.gz", ".zip"], RequiresExecutableBit: true);

    private const string ReleaseJson =
        """
        {
          "tag_name": "v1.20.071",
          "name": "PixelDeck 1.20.071",
          "body": "Cross-platform updates.",
          "html_url": "https://example.invalid/r",
          "published_at": "2026-07-28T10:00:00Z",
          "assets": [
            { "name": "PixelDeck-win-x64-1.20.071.zip", "browser_download_url": "https://e.invalid/win.zip", "size": 100 },
            { "name": "PixelDeck-win-x64-1.20.071.zip.sha256", "browser_download_url": "https://e.invalid/win.sha", "size": 64 },
            { "name": "PixelDeck-linux-arm64-1.20.071.tar.gz", "browser_download_url": "https://e.invalid/pi.tgz", "size": 200 },
            { "name": "PixelDeck-linux-arm64-1.20.071.tar.gz.sha256", "browser_download_url": "https://e.invalid/pi.sha", "size": 64 }
          ]
        }
        """;

    [Fact]
    public void WindowsPicksTheWindowsZipAndItsChecksum()
    {
        using var document = JsonDocument.Parse(ReleaseJson);

        var release = GitHubUpdateService.ParseRelease(document.RootElement, Windows);

        Assert.NotNull(release);
        Assert.Equal("PixelDeck-win-x64-1.20.071.zip", release!.AssetName);
        Assert.Equal("https://e.invalid/win.sha", release.ExpectedSha256);
    }

    [Fact]
    public void RaspberryPiPicksTheArm64TarballAndItsChecksum()
    {
        using var document = JsonDocument.Parse(ReleaseJson);

        var release = GitHubUpdateService.ParseRelease(document.RootElement, RaspberryPi);

        Assert.NotNull(release);
        Assert.Equal("PixelDeck-linux-arm64-1.20.071.tar.gz", release!.AssetName);
        // Crucially not the Windows checksum, which would fail verification.
        Assert.Equal("https://e.invalid/pi.sha", release.ExpectedSha256);
    }

    [Fact]
    public void PlatformWithNoPublishedPackageGetsNothing()
    {
        var macOs = new UpdatePlatform("osx-arm64", "PixelDeck.App", [".tar.gz"], true);
        using var document = JsonDocument.Parse(ReleaseJson);

        Assert.Null(GitHubUpdateService.ParseRelease(document.RootElement, macOs));
    }

    [Fact]
    public void TarballIsPreferredOverZipOnLinux()
    {
        // Zip does not carry the Unix execute bit, so the tarball must win.
        using var document = JsonDocument.Parse(
            """
            {
              "tag_name": "v1.20.071",
              "assets": [
                { "name": "PixelDeck-linux-arm64.zip", "browser_download_url": "https://e.invalid/a.zip", "size": 1 },
                { "name": "PixelDeck-linux-arm64.tar.gz", "browser_download_url": "https://e.invalid/a.tgz", "size": 2 }
              ]
            }
            """);

        var release = GitHubUpdateService.ParseRelease(document.RootElement, RaspberryPi);

        Assert.Equal("PixelDeck-linux-arm64.tar.gz", release!.AssetName);
    }

    [Fact]
    public void ChecksumAssetsAreNeverTreatedAsPackages()
    {
        Assert.False(Windows.Matches("PixelDeck-win-x64.zip.sha256"));
        Assert.True(Windows.Matches("PixelDeck-win-x64.zip"));
    }

    [Fact]
    public void DetectedPlatformIsInternallyConsistent()
    {
        var platform = UpdatePlatform.Detect();

        if (OperatingSystem.IsWindows())
        {
            Assert.StartsWith("win-", platform.RuntimeIdentifier, StringComparison.Ordinal);
            Assert.EndsWith(".exe", platform.ExecutableName, StringComparison.Ordinal);
            Assert.False(platform.RequiresExecutableBit);
        }
        else
        {
            Assert.DoesNotContain(".exe", platform.ExecutableName, StringComparison.Ordinal);
            Assert.True(platform.RequiresExecutableBit);
        }

        Assert.NotEmpty(platform.PackageExtensions);
    }

    [Fact]
    public void UpdatedFromArgumentIsReadFromTheCommandLine()
    {
        Assert.Equal(
            "1.20.070",
            UpdateHandoff.ReadUpdatedFromArgument(["--updated-from", "1.20.070"]));
        Assert.Null(UpdateHandoff.ReadUpdatedFromArgument(["--something-else", "x"]));
        // A trailing flag with no value must not throw.
        Assert.Null(UpdateHandoff.ReadUpdatedFromArgument(["--updated-from"]));
    }
}
