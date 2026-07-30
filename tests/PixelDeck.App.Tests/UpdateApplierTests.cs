using System.Security.Cryptography;
using PixelDeck.Launcher;

namespace PixelDeck.App.Tests;

/// <summary>
/// The launcher's install step, which replaces components during startup before
/// anything replaceable has been loaded.
/// </summary>
/// <remarks>
/// The manifest driving this comes from a GitHub release, so it is untrusted
/// input: most of these tests are about refusing a payload rather than applying
/// one.
/// </remarks>
public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"pd-apply-{Guid.NewGuid():N}");
    private readonly string _install;
    private readonly string _staging;
    private readonly List<string> _log = [];

    public UpdateApplierTests()
    {
        _install = Path.Combine(_root, "install");
        _staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(Path.Combine(_install, "Components"));
        Directory.CreateDirectory(_staging);
    }

    private UpdateApplier Applier() => new(_install, _log.Add);

    private static void Write(string root, string relative, string contents)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
    }

    private static string HashOf(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    /// <summary>Stages a file and returns the manifest entry describing it truthfully.</summary>
    private PendingFile Stage(string relative, string contents)
    {
        Write(_staging, relative, contents);
        var path = Path.Combine(_staging, relative.Replace('/', Path.DirectorySeparatorChar));
        return new PendingFile
        {
            RelativePath = relative,
            Sha256 = HashOf(contents),
            Length = new FileInfo(path).Length
        };
    }

    private PendingUpdate Pending(params PendingFile[] files) => new()
    {
        StagingFolder = _staging,
        TargetRelease = "1.22.073",
        PreviousRelease = "1.22.072",
        WaitForProcessId = null,
        Files = [.. files]
    };

    [Fact]
    public void ComponentOnlyUpdate_ReplacesTheComponentsAndNothingElse()
    {
        Write(_install, "Components/PixelDeck.App.dll", "old-app");
        Write(_install, "Components/PixelDeck.Emulation.Snes.dll", "old-snes");

        var outcome = Applier().Apply(Pending(
            Stage("Components/PixelDeck.App.dll", "new-app"),
            Stage("Components/PixelDeck.Emulation.Snes.dll", "new-snes")));

        Assert.True(outcome.Applied);
        Assert.False(outcome.LauncherReplaced);
        Assert.Null(outcome.Failure);
        Assert.Equal("new-app", File.ReadAllText(Path.Combine(_install, "Components", "PixelDeck.App.dll")));
        Assert.Equal("new-snes", File.ReadAllText(Path.Combine(_install, "Components", "PixelDeck.Emulation.Snes.dll")));
        // The backup is cleaned up once the install is confirmed.
        Assert.False(Directory.Exists(Path.Combine(_install, ".backup")));
    }

    [Fact]
    public void LauncherUpdate_RetiresTheRunningExecutableRatherThanOverwritingIt()
    {
        // Windows refuses to overwrite a running image but permits renaming it,
        // which is what lets the launcher replace itself without a helper.
        Write(_install, "PixelDeck.exe", "old-launcher");
        Write(_install, "Components/PixelDeck.App.dll", "old-app");

        var outcome = Applier().Apply(Pending(
            Stage("PixelDeck.exe", "new-launcher"),
            Stage("Components/PixelDeck.App.dll", "new-app")));

        Assert.True(outcome.Applied);
        Assert.True(outcome.LauncherReplaced);
        Assert.Equal("new-launcher", File.ReadAllText(Path.Combine(_install, "PixelDeck.exe")));
        Assert.Equal("old-launcher", File.ReadAllText(Path.Combine(_install, "PixelDeck.exe.old")));
    }

    [Fact]
    public void FailedHashVerification_RejectsTheUpdateBeforeTouchingAnything()
    {
        Write(_install, "Components/PixelDeck.App.dll", "old-app");

        var tampered = Stage("Components/PixelDeck.App.dll", "corrupted-in-transit");
        tampered.Sha256 = HashOf("what the manifest promised");

        var outcome = Applier().Apply(Pending(tampered));

        Assert.False(outcome.Applied);
        Assert.Contains("hash", outcome.Failure, StringComparison.OrdinalIgnoreCase);
        // Nothing was overwritten, so no rollback was needed.
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(_install, "Components", "PixelDeck.App.dll")));
    }

    [Fact]
    public void WrongLength_RejectsTheUpdate()
    {
        Write(_install, "Components/PixelDeck.App.dll", "old-app");

        var lying = Stage("Components/PixelDeck.App.dll", "new-app");
        lying.Length = 999_999;

        var outcome = Applier().Apply(Pending(lying));

        Assert.False(outcome.Applied);
        Assert.Contains("size", outcome.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(_install, "Components", "PixelDeck.App.dll")));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("Components/../../escaped.dll")]
    [InlineData("Games/SuperNintendo/mario.sfc")]
    [InlineData("Saves/Nintendo/zelda.srm")]
    [InlineData("Components/notes.txt")]
    public void PayloadsOutsideWhatPixelDeckOwns_AreRejected(string relativePath)
    {
        // Path traversal, and equally important, player content: a release
        // manifest must not be able to overwrite a save or a cartridge.
        var outcome = Applier().Apply(Pending(Stage(relativePath, "payload")));

        Assert.False(outcome.Applied);
        Assert.Contains("may replace", outcome.Failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingStagedFile_RejectsTheUpdate()
    {
        var absent = new PendingFile
        {
            RelativePath = "Components/PixelDeck.App.dll",
            Sha256 = HashOf("anything"),
            Length = 7
        };

        var outcome = Applier().Apply(Pending(absent));

        Assert.False(outcome.Applied);
        Assert.Contains("missing", outcome.Failure, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InterruptedCopy_RestoresThePreviousComponents()
    {
        Write(_install, "Components/PixelDeck.App.dll", "old-app");
        Write(_install, "Components/PixelDeck.Emulation.Nes.dll", "old-nes");

        var app = Stage("Components/PixelDeck.App.dll", "new-app");
        var nes = Stage("Components/PixelDeck.Emulation.Nes.dll", "new-nes");

        // Hold the second component open so the copy fails part-way, after the
        // first has already been replaced.
        var locked = Path.Combine(_install, "Components", "PixelDeck.Emulation.Nes.dll");
        using (var _ = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var outcome = Applier().Apply(Pending(app, nes));

            Assert.False(outcome.Applied);
            Assert.Contains("previous version", outcome.Failure, StringComparison.OrdinalIgnoreCase);
        }

        // The half-applied state was undone rather than left behind.
        Assert.Equal("old-app", File.ReadAllText(Path.Combine(_install, "Components", "PixelDeck.App.dll")));
    }

    [Fact]
    public void UpdateWithoutTheApplicationAssembly_IsTreatedAsAFailure()
    {
        // An install that leaves no PixelDeck.App.dll cannot start, so it must
        // roll back rather than report success.
        var outcome = Applier().Apply(Pending(Stage("Components/PixelDeck.Emulation.Nes.dll", "new-nes")));

        Assert.False(outcome.Applied);
        Assert.NotNull(outcome.Failure);
    }

    [Fact]
    public void EmptyPayload_DoesNothing()
    {
        var outcome = Applier().Apply(Pending());

        Assert.False(outcome.Applied);
        Assert.Null(outcome.Failure);
    }

    [Fact]
    public void PlayerContentSurvivesAnUpdate()
    {
        Write(_install, "Components/PixelDeck.App.dll", "old-app");
        Write(_install, "Games/SuperNintendo/mario.sfc", "cartridge");
        Write(_install, "Saves/SuperNintendo/mario.srm", "precious save");
        Write(_install, "Library/SuperNintendo/mario.png", "cover art");

        var outcome = Applier().Apply(Pending(Stage("Components/PixelDeck.App.dll", "new-app")));

        Assert.True(outcome.Applied);
        Assert.Equal("cartridge", File.ReadAllText(Path.Combine(_install, "Games", "SuperNintendo", "mario.sfc")));
        Assert.Equal("precious save", File.ReadAllText(Path.Combine(_install, "Saves", "SuperNintendo", "mario.srm")));
        Assert.Equal("cover art", File.ReadAllText(Path.Combine(_install, "Library", "SuperNintendo", "mario.png")));
    }

    [Fact]
    public void CleanUpPreviousLayout_RemovesThePreLauncherFilesAndKeepsTheRest()
    {
        // What a 1.22.072 install looks like before this release lands on it.
        Write(_install, "PixelDeck.App.exe", "old entry point");
        Write(_install, "PixelDeck.Updater.exe", "retired helper");
        Write(_install, "PixelDeck.Emulation.Snes.dll", "loose core");
        Write(_install, "PixelDeck.exe.old", "retired launcher");
        Write(_install, "libSkiaSharp.dll", "native");
        Write(_install, "PixelDeck.exe", "launcher");
        Write(_install, "Games/Nintendo/cart.nes", "rom");

        Applier().CleanUpPreviousLayout();

        Assert.False(File.Exists(Path.Combine(_install, "PixelDeck.App.exe")));
        Assert.False(File.Exists(Path.Combine(_install, "PixelDeck.Updater.exe")));
        Assert.False(File.Exists(Path.Combine(_install, "PixelDeck.Emulation.Snes.dll")));
        Assert.False(File.Exists(Path.Combine(_install, "PixelDeck.exe.old")));

        // Native libraries, the launcher, and player content are not ours to remove.
        Assert.True(File.Exists(Path.Combine(_install, "libSkiaSharp.dll")));
        Assert.True(File.Exists(Path.Combine(_install, "PixelDeck.exe")));
        Assert.True(File.Exists(Path.Combine(_install, "Games", "Nintendo", "cart.nes")));
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
            // Temp cleanup is best effort.
        }
    }
}
