using PixelDeck.App.Services;

namespace PixelDeck.App.Tests;

public sealed class SaveStateCatalogTests
{
    [Fact]
    public void EmptyCatalogCreatesNumberedSlotsInOrder()
    {
        using var directory = TemporaryDirectory.Create();
        var catalog = new SaveStateCatalog(Path.Combine(directory.Path, "game.state"));

        Assert.Empty(catalog.GetSlots());

        var first = catalog.CreateNextSlot();
        CrashSafeFile.WriteAllBytes(first.Path, [0x01]);
        var second = catalog.CreateNextSlot();

        Assert.Equal(1, first.Number);
        Assert.EndsWith(".slot-001.state", first.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, second.Number);
        Assert.EndsWith(".slot-002.state", second.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CatalogListsCommittedAndRecoverableTemporarySlots()
    {
        using var directory = TemporaryDirectory.Create();
        var catalog = new SaveStateCatalog(Path.Combine(directory.Path, "game.state"));
        var firstPath = catalog.GetSlotPath(1);
        var thirdPath = catalog.GetSlotPath(3);
        CrashSafeFile.WriteAllBytes(firstPath, [0x01]);
        File.WriteAllBytes(CrashSafeFile.GetTemporaryPath(thirdPath), [0x03]);

        var slots = catalog.GetSlots();

        Assert.Equal([1, 3], slots.Select(slot => slot.Number));
        Assert.Equal(firstPath, slots[0].Path);
        Assert.Equal(thirdPath, slots[1].Path);
    }

    [Fact]
    public void LegacySingleStateBecomesSlotOneWithoutLosingItsContents()
    {
        using var directory = TemporaryDirectory.Create();
        var legacyPath = Path.Combine(directory.Path, "game.state");
        File.WriteAllBytes(legacyPath, [0x10, 0x20, 0x30]);
        var catalog = new SaveStateCatalog(legacyPath);

        var slot = Assert.Single(catalog.GetSlots());

        Assert.Equal(1, slot.Number);
        Assert.Equal([0x10, 0x20, 0x30], File.ReadAllBytes(slot.Path));
        Assert.False(File.Exists(legacyPath));
    }

    [Fact]
    public void LegacyStateUsesTheNextFreeSlotWhenNumberedStatesExist()
    {
        using var directory = TemporaryDirectory.Create();
        var legacyPath = Path.Combine(directory.Path, "game.state");
        var catalog = new SaveStateCatalog(legacyPath);
        CrashSafeFile.WriteAllBytes(catalog.GetSlotPath(1), [0x01]);
        File.WriteAllBytes(legacyPath, [0x02]);

        var slots = catalog.GetSlots();

        Assert.Equal([1, 2], slots.Select(slot => slot.Number));
        Assert.Equal([0x02], File.ReadAllBytes(slots[1].Path));
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
