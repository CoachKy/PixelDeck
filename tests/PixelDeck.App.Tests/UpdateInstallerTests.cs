using PixelDeck.Updater;

namespace PixelDeck.App.Tests;

/// <summary>
/// File-replacement behaviour of PixelDeck.Updater, exercised against real
/// temporary directories. Rollback is the important case: a failed install must
/// leave the previous version usable.
/// </summary>
public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pd-install-{Guid.NewGuid():N}");
    private readonly string _install;
    private readonly string _staging;
    private readonly List<string> _log = [];

    public UpdateInstallerTests()
    {
        _install = Path.Combine(_root, "install");
        _staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(_install);
        Directory.CreateDirectory(_staging);
    }

    private const string Executable = "PixelDeck.App.dll";

    private InstallRequest Request() =>
        new(_staging, _install, Executable, "1.20.070", "1.20.071", WaitForProcessId: null);

    private static void WriteFile(string folder, string name, string contents)
    {
        var path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    [Fact]
    public void Install_ReplacesFilesAndRemovesTheBackup()
    {
        WriteFile(_install, Executable, "old");
        WriteFile(_install, "keep.txt", "old-data");
        WriteFile(_staging, Executable, "new");
        WriteFile(_staging, Path.Combine("Assets", "logo.png"), "art");

        var outcome = new UpdateInstaller(_log.Add).Install(Request());

        Assert.True(outcome.Succeeded);
        Assert.Null(outcome.Failure);
        Assert.Equal("new", File.ReadAllText(Path.Combine(_install, Executable)));
        // Nested content comes across too.
        Assert.True(File.Exists(Path.Combine(_install, "Assets", "logo.png")));
        // Backup is cleaned up once the install is confirmed.
        Assert.False(Directory.Exists(_install + ".backup"));
    }

    [Fact]
    public void Install_RefusesAStagingFolderWithNoExecutable()
    {
        WriteFile(_install, Executable, "old");
        WriteFile(_staging, "readme.txt", "no executable here");

        var outcome = new UpdateInstaller(_log.Add).Install(Request());

        Assert.False(outcome.Succeeded);
        Assert.NotNull(outcome.Failure);
        // The existing install is untouched.
        Assert.Equal("old", File.ReadAllText(Path.Combine(_install, Executable)));
    }

    [Fact]
    public void Install_RollsBackWhenTheCopyFails()
    {
        WriteFile(_install, Executable, "old");
        WriteFile(_install, "user-data.txt", "precious");
        WriteFile(_staging, Executable, "new");

        // Hold a file in the install folder open so the copy cannot complete.
        var blocked = Path.Combine(_install, "locked.bin");
        File.WriteAllText(blocked, "locked");
        WriteFile(_staging, "locked.bin", "replacement");

        using (var _ = new FileStream(blocked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = new UpdateInstaller(_log.Add).Install(Request());

            Assert.False(outcome.Succeeded);
            Assert.Contains("previous version", outcome.Failure, StringComparison.OrdinalIgnoreCase);
        }

        // The previous version survives the failed attempt.
        Assert.Equal("precious", File.ReadAllText(Path.Combine(_install, "user-data.txt")));
        Assert.True(File.Exists(Path.Combine(_install, Executable)));
    }

    [Fact]
    public void Install_FailureIsReportedWithoutRawExceptionText()
    {
        WriteFile(_install, Executable, "old");
        WriteFile(_staging, "nothing.txt", "x");

        var outcome = new UpdateInstaller(_log.Add).Install(Request());

        Assert.False(outcome.Succeeded);
        // Player-facing text stays plain; detail goes to the log.
        Assert.DoesNotContain("Exception", outcome.Failure!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", outcome.Failure!, StringComparison.Ordinal);
    }

    [Fact]
    public void Relaunch_ReportsFailureRatherThanThrowing()
    {
        // Nothing to launch, so this must fail quietly rather than crash the
        // updater and leave the player with no application at all.
        var relaunched = new UpdateInstaller(_log.Add)
            .Relaunch(_install, "does-not-exist-here", "1.20.070");

        Assert.False(relaunched);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            if (Directory.Exists(_install + ".backup"))
            {
                Directory.Delete(_install + ".backup", recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Temp cleanup is best effort.
        }
    }
}
