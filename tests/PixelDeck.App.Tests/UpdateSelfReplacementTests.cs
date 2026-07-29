using PixelDeck.App.Services.Updates;
using PixelDeck.Updater;

namespace PixelDeck.App.Tests;

/// <summary>
/// Covers the two ways an install can destroy itself: an updater that runs from
/// the folder it is replacing, and a backup that sweeps up the player's ROM
/// library along with the application.
/// </summary>
public sealed class UpdateSelfReplacementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pd-self-{Guid.NewGuid():N}");
    private readonly List<string> _staged = [];

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string folder, string name, string contents)
    {
        var path = Path.Combine(folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    [Fact]
    public void StageUpdater_CopiesTheUpdaterOutOfTheFolderItWillReplace()
    {
        // Windows will not let a process overwrite its own executable, so an
        // updater launched from the install folder fails the moment the copy
        // reaches PixelDeck.Updater.exe - and takes the whole update down with
        // it. The launch has to happen from somewhere else entirely.
        var install = Folder("install");
        var updater = Path.Combine(install, "PixelDeck.Updater.exe");
        File.WriteAllText(updater, "updater-binary");

        var launchFrom = UpdateHandoff.StageUpdater(updater);
        Assert.NotNull(launchFrom);
        _staged.Add(launchFrom!);

        Assert.True(File.Exists(launchFrom));
        Assert.Equal("updater-binary", File.ReadAllText(launchFrom!));

        // The whole point: it must not sit inside the folder being replaced.
        var stagedFolder = Path.GetDirectoryName(Path.GetFullPath(launchFrom!))!;
        Assert.False(
            stagedFolder.StartsWith(Path.GetFullPath(install), StringComparison.OrdinalIgnoreCase),
            "the updater was staged inside the folder it is meant to replace");
    }

    [Fact]
    public void Install_ReplacesTheUpdatersOwnExecutable()
    {
        // The staged package contains an updater of its own, so a successful
        // install has to leave the new one in place rather than skipping it.
        var install = Folder("install");
        var staging = Folder("staging");

        WriteFile(install, "PixelDeck.App.dll", "old-app");
        WriteFile(install, "PixelDeck.Updater.exe", "old-updater");
        WriteFile(staging, "PixelDeck.App.dll", "new-app");
        WriteFile(staging, "PixelDeck.Updater.exe", "new-updater");

        var outcome = new UpdateInstaller(_ => { }).Install(new InstallRequest(
            staging, install, "PixelDeck.App.dll", "1.22.070", "1.22.071", WaitForProcessId: null));

        Assert.True(outcome.Succeeded);
        Assert.Equal("new-updater", File.ReadAllText(Path.Combine(install, "PixelDeck.Updater.exe")));
    }

    [Fact]
    public void Install_DoesNotBackUpThePlayersGamesAndSaves()
    {
        // A ROM library can be tens of gigabytes. Nothing in the staged package
        // touches it, so copying it aside is pure cost - and on a Raspberry Pi's
        // SD card it could plausibly run the disk out of space mid-update.
        var install = Folder("install");
        var staging = Folder("staging");

        WriteFile(install, "PixelDeck.App.dll", "old-app");
        WriteFile(install, Path.Combine("Games", "SuperNintendo", "big.sfc"), "a very large cartridge");
        WriteFile(install, Path.Combine("Saves", "SuperNintendo", "big.srm"), "precious save");
        WriteFile(staging, "PixelDeck.App.dll", "new-app");

        string? backupSeen = null;
        var outcome = new UpdateInstaller(message =>
        {
            if (message.Contains(".backup", StringComparison.Ordinal))
            {
                backupSeen = message;
            }
        }).Install(new InstallRequest(
            staging, install, "PixelDeck.App.dll", "1.22.070", "1.22.071", WaitForProcessId: null));

        Assert.True(outcome.Succeeded);
        Assert.NotNull(backupSeen);

        // Player content survives untouched...
        Assert.Equal(
            "a very large cartridge",
            File.ReadAllText(Path.Combine(install, "Games", "SuperNintendo", "big.sfc")));
        Assert.Equal(
            "precious save",
            File.ReadAllText(Path.Combine(install, "Saves", "SuperNintendo", "big.srm")));
        Assert.Equal("new-app", File.ReadAllText(Path.Combine(install, "PixelDeck.App.dll")));
    }

    [Fact]
    public void Install_BackupCountsOnlyTheFilesBeingOverwritten()
    {
        // The backup is deleted once the install settles either way, so its size
        // is reported rather than inspected. Two application files are replaced;
        // the cartridge and its save are not, and must not be counted.
        var install = Folder("install");
        var staging = Folder("staging");

        WriteFile(install, "PixelDeck.App.dll", "old-app");
        WriteFile(install, "PixelDeck.Updater.exe", "old-updater");
        WriteFile(install, Path.Combine("Games", "Nintendo", "cart.nes"), "rom");
        WriteFile(install, Path.Combine("Saves", "Nintendo", "cart.srm"), "save");
        WriteFile(staging, "PixelDeck.App.dll", "new-app");
        WriteFile(staging, "PixelDeck.Updater.exe", "new-updater");

        var log = new List<string>();
        var outcome = new UpdateInstaller(log.Add).Install(new InstallRequest(
            staging, install, "PixelDeck.App.dll", "1.22.070", "1.22.071", WaitForProcessId: null));

        Assert.True(outcome.Succeeded);
        Assert.Contains(log, line => line.Contains("Backed up 2 replaced file(s)", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        foreach (var path in _staged)
        {
            try
            {
                var folder = Path.GetDirectoryName(path);
                if (folder is not null && Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var folder in new[] { _root, _root + "\\install.backup", Path.Combine(_root, "install") + ".backup" })
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
