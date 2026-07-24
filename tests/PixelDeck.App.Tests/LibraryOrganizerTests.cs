using Avalonia.Media;
using PixelDeck.App.Models;

namespace PixelDeck.App.Tests;

public sealed class LibraryOrganizerTests
{
    [Fact]
    public void CreateSections_GroupsNumbersAndSymbolsFirstThenLetters()
    {
        var sections = LibraryOrganizer.CreateSections(
        [
            CreateGame("delta"),
            CreateGame("Beta"),
            CreateGame("apple"),
            CreateGame("Alpha"),
            CreateGame("10-Yard Fight"),
            CreateGame("! Homebrew")
        ]);

        Assert.Equal(["#", "A", "B", "D"], sections.Select(section => section.Label));
        Assert.Equal(["! Homebrew", "10-Yard Fight"], sections[0].Games.Select(game => game.Title));
        Assert.Equal(["Alpha", "apple"], sections[1].Games.Select(game => game.Title));
        Assert.Equal("2 TITLES", sections[0].TitleCountText);
        Assert.Equal("1 TITLE", sections[2].TitleCountText);
    }

    [Theory]
    [InlineData(0, "NO TITLES")]
    [InlineData(1, "1 TITLE")]
    [InlineData(25, "25 TITLES")]
    public void FormatTitleCount_UsesLibraryLanguage(int count, string expected) =>
        Assert.Equal(expected, LibraryOrganizer.FormatTitleCount(count));

    [Fact]
    public void TryGetAdjacentGame_NavigatesAcrossUnevenLetterSections()
    {
        var aGames = Enumerable.Range(0, 8).Select(index => CreateGame($"A{index}")).ToArray();
        var bGames = Enumerable.Range(0, 3).Select(index => CreateGame($"B{index}")).ToArray();
        var sections = LibraryOrganizer.CreateSections(aGames.Concat(bGames));

        Assert.True(LibraryOrganizer.TryGetAdjacentGame(sections, aGames[1], 1, 6, out var secondARow));
        Assert.Same(aGames[7], secondARow);

        Assert.True(LibraryOrganizer.TryGetAdjacentGame(sections, aGames[5], 1, 6, out var partialARow));
        Assert.Same(aGames[7], partialARow);

        Assert.True(LibraryOrganizer.TryGetAdjacentGame(sections, aGames[7], 1, 6, out var firstBRow));
        Assert.Same(bGames[1], firstBRow);

        Assert.True(LibraryOrganizer.TryGetAdjacentGame(sections, bGames[1], -1, 6, out var previousSection));
        Assert.Same(aGames[7], previousSection);

        Assert.False(LibraryOrganizer.TryGetAdjacentGame(sections, aGames[1], -1, 6, out _));
    }

    private static GameEntry CreateGame(string title) => new(
        title,
        "Test System",
        "TEST",
        $"{title}.rom",
        Path.GetFullPath($"{title}.rom"),
        $"{title}.rom",
        "1 KB",
        "JAN 1, 2026",
        Colors.CornflowerBlue);
}
