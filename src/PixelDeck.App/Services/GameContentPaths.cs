namespace PixelDeck.App.Services;

/// <summary>
/// Path rules shared by the per-game folders that sit alongside the games
/// themselves — saves and library images.
/// </summary>
/// <remarks>
/// Both mirror the library's own platform layout so their files stay
/// recognisable by name rather than by hash, and both have to refuse a game
/// path that would resolve outside its own root.
/// </remarks>
internal static class GameContentPaths
{
    /// <summary>
    /// Strips the leading platform folder from a library-relative game path, so
    /// the remainder can be rebuilt underneath a different root.
    /// </summary>
    public static string RemovePlatformFolder(string relativeGamePath, string platformFolderName)
    {
        var normalized = relativeGamePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var prefix = platformFolderName + Path.DirectorySeparatorChar;
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..]
            : normalized;
    }

    /// <summary>
    /// Resolves a relative path under a root, refusing anything that escapes
    /// it. Game filenames come from disk and may contain traversal sequences.
    /// </summary>
    public static string GetContained(string rootFolder, string relativePath, string rootDescription)
    {
        var rootPrefix = Path.GetFullPath(rootFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(rootPrefix, relativePath));
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"The game path resolves outside its {rootDescription}.");
        }

        return candidate;
    }
}
