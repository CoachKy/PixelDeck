using Avalonia.Media;
using PixelDeck.App.Models;
using PixelDeck.App.Services;

namespace PixelDeck.App.Tests;

public sealed class PlayHistoryStoreTests
{
    [Fact]
    public void GameEntry_FormatsAttachedLibraryPlayHistory()
    {
        var game = CreateGame(Path.Combine("Nintendo", "Homebrew.nes"));
        var changedProperties = new List<string>();
        game.PropertyChanged += (_, eventArgs) => changedProperties.Add(eventArgs.PropertyName!);
        var lastPlayed = new DateTime(2026, 7, 22, 20, 0, 0, DateTimeKind.Utc);

        game.UpdatePlayHistory(TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(7)).Ticks, 3, lastPlayed);

        Assert.Equal("2H 7M PLAYED", game.PlayTimeText);
        Assert.Equal(TimeSpan.FromHours(2).Add(TimeSpan.FromMinutes(7)), game.TotalPlayTime);
        Assert.Equal(3, game.SessionCount);
        Assert.Equal(lastPlayed, game.LastPlayedUtc);
        Assert.NotEqual("NEVER PLAYED", game.LastPlayedText);
        Assert.Contains(nameof(GameEntry.PlayTimeText), changedProperties);

        game.UpdatePlayHistory(0, 0, null);

        Assert.Equal("NOT PLAYED YET", game.PlayTimeText);
        Assert.Equal("NEVER PLAYED", game.LastPlayedText);
    }

    [Fact]
    public void RecordSession_AggregatesRealSessionsForTheSameGame()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var store = new PlayHistoryStore(Path.Combine(testRoot, "play-history.json"));
            var game = CreateGame(Path.Combine("Nintendo", "Homebrew.nes"));
            var firstEnd = new DateTime(2026, 7, 21, 20, 0, 0, DateTimeKind.Utc);
            var secondEnd = firstEnd.AddDays(1);

            store.RecordSession(game, TimeSpan.FromMinutes(4), firstEnd);
            store.RecordSession(game with { RelativePath = "nintendo\\homebrew.nes" }, TimeSpan.FromMinutes(6), secondEnd);

            var history = Assert.Single(store.Read());
            Assert.Equal(TimeSpan.FromMinutes(10), TimeSpan.FromTicks(history.TotalPlayTimeTicks));
            Assert.Equal(2, history.SessionCount);
            Assert.Equal(secondEnd, history.LastPlayedUtc);
            Assert.Equal("nintendo\\homebrew.nes", history.RelativePath);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    private static GameEntry CreateGame(string relativePath) => new(
        "Homebrew",
        "Nintendo Entertainment System",
        "NES",
        "Homebrew.nes",
        Path.Combine("C:\\Games", relativePath),
        relativePath,
        "16 KB",
        "JUL 22, 2026",
        Colors.CornflowerBlue);

    private static string CreateTestDirectory()
    {
        var testRoot = Path.Combine(Path.GetTempPath(), "PixelDeck.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        return testRoot;
    }

    private static void DeleteTestDirectory(string testRoot)
    {
        var testParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PixelDeck.Tests"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolvedRoot = Path.GetFullPath(testRoot);

        if (!resolvedRoot.StartsWith(testParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove a directory outside the PixelDeck test area.");
        }

        if (Directory.Exists(resolvedRoot))
        {
            Directory.Delete(resolvedRoot, recursive: true);
        }
    }
}
