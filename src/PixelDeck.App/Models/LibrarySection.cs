namespace PixelDeck.App.Models;

public sealed record LibrarySection(string Label, IReadOnlyList<GameEntry> Games)
{
    public string TitleCountText => LibraryOrganizer.FormatTitleCount(Games.Count);
}

public static class LibraryOrganizer
{
    public static IReadOnlyList<LibrarySection> CreateSections(IEnumerable<GameEntry> games)
    {
        ArgumentNullException.ThrowIfNull(games);

        return games
            .OrderBy(game => GetSectionSortOrder(GetSectionLabel(game.Title)))
            .ThenBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(game => game.RelativePath, StringComparer.OrdinalIgnoreCase)
            .GroupBy(game => GetSectionLabel(game.Title))
            .Select(group => new LibrarySection(group.Key, group.ToArray()))
            .ToArray();
    }

    public static string FormatTitleCount(int count) => count switch
    {
        <= 0 => "NO TITLES",
        1 => "1 TITLE",
        _ => $"{count} TITLES"
    };

    public static int GetSectionIndex(
        IReadOnlyList<LibrarySection> sections,
        GameEntry? game)
    {
        if (game is null)
        {
            return -1;
        }

        for (var sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
        {
            if (IndexOfReference(sections[sectionIndex].Games, game) >= 0)
            {
                return sectionIndex;
            }
        }

        return -1;
    }

    public static bool IsGameInFirstColumn(
        IReadOnlyList<LibrarySection> sections,
        GameEntry? game,
        int columnCount)
    {
        if (game is null || columnCount <= 0)
        {
            return false;
        }

        var sectionIndex = GetSectionIndex(sections, game);
        if (sectionIndex < 0)
        {
            return false;
        }

        var gameIndex = IndexOfReference(sections[sectionIndex].Games, game);
        return gameIndex >= 0 && gameIndex % columnCount == 0;
    }

    public static int MoveSectionIndex(int currentIndex, int direction, int sectionCount)
    {
        if (sectionCount <= 0)
        {
            return -1;
        }

        if (currentIndex < 0 || currentIndex >= sectionCount)
        {
            return 0;
        }

        return Math.Clamp(currentIndex + Math.Sign(direction), 0, sectionCount - 1);
    }

    public static bool TryGetAdjacentGame(
        IReadOnlyList<LibrarySection> sections,
        GameEntry? currentGame,
        int direction,
        int columnCount,
        out GameEntry? adjacentGame)
    {
        adjacentGame = null;
        if (sections.Count == 0 || currentGame is null || direction == 0 || columnCount <= 0)
        {
            return false;
        }

        var sectionIndex = -1;
        var gameIndex = -1;
        for (var index = 0; index < sections.Count; index++)
        {
            gameIndex = IndexOfReference(sections[index].Games, currentGame);
            if (gameIndex >= 0)
            {
                sectionIndex = index;
                break;
            }
        }

        if (sectionIndex < 0)
        {
            return false;
        }

        var step = Math.Sign(direction);
        var section = sections[sectionIndex];
        var column = gameIndex % columnCount;
        var targetIndex = gameIndex + (step * columnCount);
        if (targetIndex >= 0 && targetIndex < section.Games.Count)
        {
            adjacentGame = section.Games[targetIndex];
            return true;
        }

        if (step > 0)
        {
            var nextRowStart = ((gameIndex / columnCount) + 1) * columnCount;
            if (nextRowStart < section.Games.Count)
            {
                adjacentGame = section.Games[^1];
                return true;
            }
        }

        var adjacentSectionIndex = sectionIndex + step;
        if (adjacentSectionIndex < 0 || adjacentSectionIndex >= sections.Count)
        {
            return false;
        }

        var adjacentSection = sections[adjacentSectionIndex];
        if (adjacentSection.Games.Count == 0)
        {
            return false;
        }

        if (step > 0)
        {
            adjacentGame = adjacentSection.Games[Math.Min(column, adjacentSection.Games.Count - 1)];
            return true;
        }

        var lastRowStart = ((adjacentSection.Games.Count - 1) / columnCount) * columnCount;
        var lastRowCount = adjacentSection.Games.Count - lastRowStart;
        adjacentGame = adjacentSection.Games[lastRowStart + Math.Min(column, lastRowCount - 1)];
        return true;
    }

    private static string GetSectionLabel(string title)
    {
        var trimmedTitle = title.TrimStart();
        if (trimmedTitle.Length == 0)
        {
            return "#";
        }

        var first = char.ToUpperInvariant(trimmedTitle[0]);
        return first is >= 'A' and <= 'Z' ? first.ToString() : "#";
    }

    private static int GetSectionSortOrder(string label) =>
        label == "#" ? 0 : label[0] - 'A' + 1;

    private static int IndexOfReference(IReadOnlyList<GameEntry> games, GameEntry target)
    {
        for (var index = 0; index < games.Count; index++)
        {
            if (ReferenceEquals(games[index], target))
            {
                return index;
            }
        }

        return -1;
    }
}
