namespace PixelDeck.App.Services;

internal sealed class GameSaveStorage
{
    private const string SavesFolderName = "Saves";
    private readonly string _gamesFolder;

    public GameSaveStorage(string gamesFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gamesFolder);
        _gamesFolder = Path.GetFullPath(gamesFolder);
        var gamesParent = Directory.GetParent(_gamesFolder)?.FullName
            ?? throw new ArgumentException("The games folder needs a parent directory.", nameof(gamesFolder));

        RootFolder = Directory.CreateDirectory(Path.Combine(gamesParent, SavesFolderName)).FullName;
    }

    public string RootFolder { get; }

    public string EnsurePlatformFolder(string platformFolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platformFolderName);
        return Directory.CreateDirectory(Path.Combine(RootFolder, platformFolderName)).FullName;
    }

    public GameSavePaths GetPaths(
        string relativeGamePath,
        string platformFolderName,
        string batterySaveExtension,
        string legacyCacheKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeGamePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(platformFolderName);
        ArgumentException.ThrowIfNullOrWhiteSpace(batterySaveExtension);
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyCacheKey);

        var platformRelativePath = RemovePlatformFolder(relativeGamePath, platformFolderName);
        var relativeBasePath = Path.ChangeExtension(platformRelativePath, null)
            ?? throw new ArgumentException("The game path needs a filename.", nameof(relativeGamePath));
        var platformFolder = EnsurePlatformFolder(platformFolderName);
        var destinationBasePath = GetContainedPath(platformFolder, relativeBasePath);
        var paths = new GameSavePaths(
            destinationBasePath + NormalizeExtension(batterySaveExtension),
            destinationBasePath + ".state");

        MigrateLegacyFiles(paths, legacyCacheKey);
        return paths;
    }

    private void MigrateLegacyFiles(GameSavePaths paths, string legacyCacheKey)
    {
        var legacyBatteryPath = Path.Combine(
            _gamesFolder,
            ".pixeldeck",
            "saves",
            legacyCacheKey + ".sav");
        MoveIfDestinationIsEmpty(legacyBatteryPath, paths.BatteryPath);
        MoveIfDestinationIsEmpty(legacyBatteryPath + ".tmp", paths.BatteryPath + ".tmp");

        var legacyStateFolder = Path.Combine(_gamesFolder, ".pixeldeck", "screenshots");
        if (!Directory.Exists(legacyStateFolder))
        {
            return;
        }

        foreach (var legacyPath in Directory.EnumerateFiles(
                     legacyStateFolder,
                     legacyCacheKey + "*.state*",
                     SearchOption.TopDirectoryOnly))
        {
            var legacyFileName = Path.GetFileName(legacyPath);
            var suffix = legacyFileName[legacyCacheKey.Length..];
            var destinationBasePath = Path.ChangeExtension(paths.StatePath, null)!;
            MoveIfDestinationIsEmpty(legacyPath, destinationBasePath + suffix);
        }
    }

    private static void MoveIfDestinationIsEmpty(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath) || File.Exists(destinationPath))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Move(sourcePath, destinationPath);
        }
        catch (IOException)
        {
            // Another scan or process may have migrated the file first. Never
            // overwrite either copy when their contents cannot be compared.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the legacy copy intact if this installation cannot move it.
        }
    }

    private static string RemovePlatformFolder(string relativeGamePath, string platformFolderName)
    {
        var normalized = relativeGamePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var prefix = platformFolderName + Path.DirectorySeparatorChar;
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[prefix.Length..]
            : normalized;
    }

    private static string GetContainedPath(string rootFolder, string relativePath)
    {
        var rootPrefix = Path.GetFullPath(rootFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(rootPrefix, relativePath));
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The game path resolves outside its save folder.");
        }

        return candidate;
    }

    private static string NormalizeExtension(string extension) =>
        extension.StartsWith('.') ? extension : "." + extension;
}

internal sealed record GameSavePaths(string BatteryPath, string StatePath);
