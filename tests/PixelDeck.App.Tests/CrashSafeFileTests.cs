using PixelDeck.App.Services;

namespace PixelDeck.App.Tests;

public sealed class CrashSafeFileTests
{
    [Fact]
    public void WriteCommitsTheCompleteFileAndRemovesTheTemporaryCopy()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "slot.state");

        CrashSafeFile.WriteAllBytes(path, [0x10, 0x20, 0x30]);

        Assert.Equal([0x10, 0x20, 0x30], File.ReadAllBytes(path));
        Assert.False(File.Exists(CrashSafeFile.GetTemporaryPath(path)));
    }

    [Fact]
    public void InterruptedReplacementKeepsTheCommittedCopyFirst()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "slot.state");
        CrashSafeFile.WriteAllBytes(path, [0x10, 0x20, 0x30]);
        File.WriteAllBytes(CrashSafeFile.GetTemporaryPath(path), [0xFF]);

        var candidates = CrashSafeFile.GetReadCandidates(path);

        Assert.Equal([path, CrashSafeFile.GetTemporaryPath(path)], candidates);
        Assert.Equal([0x10, 0x20, 0x30], File.ReadAllBytes(candidates[0]));
    }

    [Fact]
    public void CompleteTemporaryCopyCanBeRecoveredAfterAnInterruptedCommit()
    {
        using var directory = TemporaryDirectory.Create();
        var path = Path.Combine(directory.Path, "slot.state");
        var temporaryPath = CrashSafeFile.GetTemporaryPath(path);
        File.WriteAllBytes(temporaryPath, [0x41, 0x42, 0x43]);

        Assert.True(CrashSafeFile.Exists(path));
        Assert.Equal([temporaryPath], CrashSafeFile.GetReadCandidates(path));

        CrashSafeFile.CommitTemporary(path);

        Assert.Equal([0x41, 0x42, 0x43], File.ReadAllBytes(path));
        Assert.False(File.Exists(temporaryPath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "PixelDeck.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose()
        {
            var parent = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PixelDeck.Tests"))
                .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to remove a directory outside the PixelDeck test area.");
            }

            if (Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
