using PixelDeck.App.Services.Updates;

namespace PixelDeck.App.Tests;

/// <summary>
/// Choosing between a component-only update and the full package, decided from
/// the release's asset names.
/// </summary>
public sealed class ComponentArchiveTests
{
    [Theory]
    [InlineData("PixelDeck-v1.22.073-components-launcher1.0.0.zip", "1.0.0")]
    [InlineData("PixelDeck-v1.23.100-components-launcher2.4.11.zip", "2.4.11")]
    [InlineData("pixeldeck-v1.22.073-components-launcher1.0.zip", "1.0")]
    public void ComponentArchives_AreRecognisedWithTheirTargetLauncher(string assetName, string expected)
    {
        Assert.True(ComponentArchive.TryMatch(assetName, out var launcher));
        Assert.Equal(Version.Parse(expected), launcher);
    }

    [Theory]
    [InlineData("PixelDeck-v1.22.073-win-x64.zip")]
    [InlineData("PixelDeck-v1.22.073-linux-arm64.tar.gz")]
    [InlineData("manifest.json")]
    [InlineData("PixelDeck-v1.22.073-components.zip")]
    [InlineData("PixelDeck-v1.22.073-components-launcher.zip")]
    public void EverythingElse_IsNotAComponentArchive(string assetName)
    {
        // The full packages in particular must never be mistaken for one: doing
        // so would stage a launcher update as though it were components only.
        Assert.False(ComponentArchive.TryMatch(assetName, out _));
    }

    [Fact]
    public void AnArchiveBuiltForADifferentLauncher_IsNotUsable()
    {
        // The launcher under test reports its own version; an archive naming a
        // different one has to fall back to the full package, because its
        // components may expect a launcher contract this one does not implement.
        Assert.False(ComponentArchive.IsUsableHere(
            "PixelDeck-v1.22.073-components-launcher99.0.0.zip"));
    }

    [Fact]
    public void AnArchiveForTheRunningLauncher_IsUsable()
    {
        var running = ComponentArchive.RunningLauncherVersion;
        Assert.NotNull(running);

        // Written the way the publish script writes it: three components, while
        // the assembly reports four. The trailing revision must not cause a
        // mismatch that forces a full download.
        var name = $"PixelDeck-v1.22.073-components-launcher{running!.Major}.{running.Minor}.{Math.Max(running.Build, 0)}.zip";

        Assert.True(ComponentArchive.IsUsableHere(name));
    }
}
