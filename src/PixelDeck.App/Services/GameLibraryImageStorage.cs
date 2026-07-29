namespace PixelDeck.App.Services;

/// <summary>
/// Stores the pictures shown on library cards when the player has chosen them
/// deliberately, in a visible <c>Library</c> folder beside <c>Games</c> and
/// <c>Saves</c>.
/// </summary>
/// <remarks>
/// These are authored content rather than cache: a captured library image is
/// something the player picked and would miss if it vanished, so it is kept
/// next to their saves under a readable name instead of in the hidden
/// <c>.pixeldeck</c> cache under a hash. Automatic boot screenshots stay in the
/// cache, because regenerating one costs nothing.
/// </remarks>
internal sealed class GameLibraryImageStorage
{
    private const string LibraryFolderName = "Library";

    public GameLibraryImageStorage(string gamesFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamesFolder);
        var gamesParent = Directory.GetParent(Path.GetFullPath(gamesFolder))?.FullName
            ?? throw new ArgumentException("The games folder needs a parent directory.", nameof(gamesFolder));

        RootFolder = Path.Combine(gamesParent, LibraryFolderName);
    }

    public string RootFolder { get; }

    /// <summary>
    /// Creates the platform folders so the layout is discoverable. Because the
    /// filenames mirror the games, a player can drop a picture in by hand and
    /// it becomes that game's library image without any import step.
    /// </summary>
    public string EnsurePlatformFolder(string platformFolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformFolderName);
        return Directory.CreateDirectory(Path.Combine(RootFolder, platformFolderName)).FullName;
    }

    /// <summary>
    /// Where a given game's library image lives. Directories are not created
    /// here; a scan should not litter folders for games with no image.
    /// </summary>
    public string GetImagePath(string relativeGamePath, string platformFolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeGamePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformFolderName);

        var platformRelativePath = GameContentPaths.RemovePlatformFolder(relativeGamePath, platformFolderName);
        var relativeBasePath = Path.ChangeExtension(platformRelativePath, null)
            ?? throw new ArgumentException("The game path needs a filename.", nameof(relativeGamePath));

        var platformFolder = Path.Combine(RootFolder, platformFolderName);
        return GameContentPaths.GetContained(platformFolder, relativeBasePath, "library image folder") + ".png";
    }

    /// <summary>
    /// Moves an image captured before library images had their own folder out
    /// of the hidden cache, so an early capture is not silently orphaned.
    /// </summary>
    public static void MigrateLegacyImage(string legacyCachePath, string destinationPath)
    {
        if (!File.Exists(legacyCachePath) || File.Exists(destinationPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Move(legacyCachePath, destinationPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Leave the original in place if it cannot be moved.
        }
    }
}
