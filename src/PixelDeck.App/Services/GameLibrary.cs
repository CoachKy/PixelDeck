using System.Security.Cryptography;
using System.Text;
using Avalonia.Media;
using PixelDeck.App.Models;
using PixelDeck.Emulation.GameCube;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.N64;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Services;

public sealed class GameLibrary
{
    public const string NintendoFolderName = "Nintendo";
    public const string Nintendo64FolderName = "Nintendo64";
    public const string SuperNintendoFolderName = "SuperNintendo";
    public const string GameCubeFolderName = "GameCube";

    private static readonly string[] ScreenshotExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    private static readonly Dictionary<string, PlatformDefinition> Platforms =
        new Dictionary<string, PlatformDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [".nes"] = new("Nintendo Entertainment System", "NES", Color.Parse("#F04464"), NintendoFolderName),
            [".fds"] = new("Famicom Disk System", "FDS", Color.Parse("#E9573F"), NintendoFolderName),
            [".sfc"] = new("Super Nintendo Entertainment System", "SNES", Color.Parse("#8B7CF6"), SuperNintendoFolderName),
            [".smc"] = new("Super Nintendo Entertainment System", "SNES", Color.Parse("#8B7CF6"), SuperNintendoFolderName),
            [".gb"] = new("Game Boy", "GB", Color.Parse("#9BBF45")),
            [".gbc"] = new("Game Boy Color", "GBC", Color.Parse("#F0A43A")),
            [".gba"] = new("Game Boy Advance", "GBA", Color.Parse("#6C63D9")),
            [".n64"] = new("Nintendo 64", "N64", Color.Parse("#38B7A5"), Nintendo64FolderName),
            [".z64"] = new("Nintendo 64", "N64", Color.Parse("#38B7A5"), Nintendo64FolderName),
            [".v64"] = new("Nintendo 64", "N64", Color.Parse("#38B7A5"), Nintendo64FolderName),
            [".nds"] = new("Nintendo DS", "NDS", Color.Parse("#5A9CF8")),
            [".gcm"] = new("Nintendo GameCube", "GC", Color.Parse("#6D72E8")),
            [".ciso"] = new("Nintendo GameCube", "GC", Color.Parse("#6D72E8")),
            [".rvz"] = new("Nintendo GameCube / Wii", "GC/WII", Color.Parse("#4CB4D8")),
            [".wbfs"] = new("Nintendo Wii", "WII", Color.Parse("#68C5D6")),
            [".iso"] = new("Disc Image", "DISC", Color.Parse("#5E83A8")),
            [".dol"] = new("Nintendo Homebrew", "DOL", Color.Parse("#F28B42")),
            [".elf"] = new("Homebrew", "ELF", Color.Parse("#E4B63E"))
        };

    /// <summary>
    /// The GameCube platform as it appears once a disc is filed under
    /// <c>Games/GameCube</c>: named, badged, and given somewhere to keep its
    /// saves and library image.
    /// </summary>
    private static readonly PlatformDefinition GameCubePlatform =
        new("Nintendo GameCube", "GC", Color.Parse("#6D72E8"), GameCubeFolderName);

    /// <summary>
    /// Disc containers that mean GameCube when they are filed under the
    /// GameCube folder.
    /// </summary>
    /// <remarks>
    /// <c>.iso</c> is the reason this is folder-aware rather than another row
    /// in the extension table. It is the ordinary extension for a GameCube
    /// disc and also the ordinary extension for a disc image of anything
    /// else, so where the player put it is the only evidence of what it is —
    /// and it is evidence the player supplied deliberately.
    /// </remarks>
    private static readonly HashSet<string> GameCubeDiscExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".iso", ".gcm", ".ciso", ".gcz", ".rvz" };

    private readonly RomTitleResolver _titleResolver;
    private readonly GameSaveStorage _saveStorage;
    private readonly GameLibraryImageStorage _libraryImageStorage;

    public GameLibrary(string? gamesFolder = null)
    {
        GamesFolder = gamesFolder is null ? ResolveGamesFolder() : Path.GetFullPath(gamesFolder);
        Directory.CreateDirectory(GamesFolder);
        NintendoFolder = Directory.CreateDirectory(Path.Combine(GamesFolder, NintendoFolderName)).FullName;
        Nintendo64Folder = Directory.CreateDirectory(Path.Combine(GamesFolder, Nintendo64FolderName)).FullName;
        SuperNintendoFolder = Directory.CreateDirectory(Path.Combine(GamesFolder, SuperNintendoFolderName)).FullName;
        GameCubeFolder = Directory.CreateDirectory(Path.Combine(GamesFolder, GameCubeFolderName)).FullName;
        _titleResolver = new RomTitleResolver(GamesFolder);
        _saveStorage = new GameSaveStorage(GamesFolder);
        _libraryImageStorage = new GameLibraryImageStorage(GamesFolder);
        _libraryImageStorage.EnsurePlatformFolder(NintendoFolderName);
        _libraryImageStorage.EnsurePlatformFolder(Nintendo64FolderName);
        _libraryImageStorage.EnsurePlatformFolder(SuperNintendoFolderName);
        _libraryImageStorage.EnsurePlatformFolder(GameCubeFolderName);
        NintendoSavesFolder = _saveStorage.EnsurePlatformFolder(NintendoFolderName);
        Nintendo64SavesFolder = _saveStorage.EnsurePlatformFolder(Nintendo64FolderName);
        SuperNintendoSavesFolder = _saveStorage.EnsurePlatformFolder(SuperNintendoFolderName);
        GameCubeSavesFolder = _saveStorage.EnsurePlatformFolder(GameCubeFolderName);
    }

    public string GamesFolder { get; }

    public string NintendoFolder { get; }

    public string Nintendo64Folder { get; }

    public string SuperNintendoFolder { get; }

    public string GameCubeFolder { get; }

    public string SavesFolder => _saveStorage.RootFolder;

    /// <summary>Visible folder holding library images the player captured.</summary>
    public string LibraryImagesFolder => _libraryImageStorage.RootFolder;

    public string NintendoSavesFolder { get; }

    public string Nintendo64SavesFolder { get; }

    public string SuperNintendoSavesFolder { get; }

    public string GameCubeSavesFolder { get; }

    public string MetadataFolder => _titleResolver.CatalogFolder;

    public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<GameEntry>>(() => Scan(cancellationToken), cancellationToken);

    private GameEntry[] Scan(CancellationToken cancellationToken)
    {
        var results = new List<GameEntry>();
        _titleResolver.RefreshCatalogs();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };

        foreach (var filePath in Directory.EnumerateFiles(GamesFolder, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(filePath);
            var relativeFilePath = Path.GetRelativePath(GamesFolder, filePath);
            if (!TryResolvePlatform(extension, relativeFilePath, out var platform))
            {
                continue;
            }

            try
            {
                var file = new FileInfo(filePath);
                var relativePath = Path.GetRelativePath(GamesFolder, file.FullName);
                var cacheKey = GetCacheKey(relativePath);
                var screenshotCachePath = GetCachePath("screenshots", cacheKey, ".png");
                var libraryImagePath = platform.SaveFolderName is null
                    ? string.Empty
                    : _libraryImageStorage.GetImagePath(relativePath, platform.SaveFolderName);
                if (libraryImagePath.Length > 0)
                {
                    GameLibraryImageStorage.MigrateLegacyImage(
                        GetCachePath("library", cacheKey, ".png"),
                        libraryImagePath);
                }

                var screenshotPath = FindScreenshot(file, libraryImagePath, screenshotCachePath);
                var hasChosenLibraryImage =
                    HasSidecarImage(file) ||
                    (libraryImagePath.Length > 0 && File.Exists(libraryImagePath));
                var compatibility = InspectCompatibility(file.FullName, extension, platform.Code);
                var fallbackTitle = CleanTitle(Path.GetFileNameWithoutExtension(file.Name));
                var title = _titleResolver.Resolve(
                    file,
                    relativePath,
                    extension,
                    fallbackTitle,
                    compatibility.CartridgeTitle);
                var savePaths = platform.SaveFolderName is null
                    ? null
                    : _saveStorage.GetPaths(
                        relativePath,
                        platform.SaveFolderName,
                        compatibility.BatterySaveExtension,
                        cacheKey);

                results.Add(new GameEntry(
                    title,
                    platform.Name,
                    platform.Code,
                    file.Name,
                    file.FullName,
                    relativePath,
                    FormatSize(file.Length),
                    file.LastWriteTime.ToString("MMM d, yyyy").ToUpperInvariant(),
                    platform.AccentColor)
                {
                    ScreenshotPath = screenshotPath,
                    ScreenshotCachePath = screenshotCachePath,
                    LibraryImagePath = libraryImagePath,
                    HasChosenLibraryImage = hasChosenLibraryImage,
                    SaveRamPath = savePaths?.BatteryPath ?? string.Empty,
                    SaveStatePath = savePaths?.StatePath ?? string.Empty,
                    MapperNumber = compatibility.MapperNumber,
                    SubmapperNumber = compatibility.SubmapperNumber,
                    CartridgeDescription = compatibility.CartridgeDescription,
                    IsCoreSupported = compatibility.IsSupported,
                    IsLimitedCompatibility = compatibility.IsLimited,
                    CompatibilityText = compatibility.Message
                });
            }
            catch (IOException)
            {
                // The file changed during the scan. It will be picked up next time.
            }
            catch (UnauthorizedAccessException)
            {
                // Ignore individual files the current user cannot inspect.
            }
        }

        _titleResolver.SaveCache();
        return results
            .OrderBy(game => game.Platform)
            .ThenBy(game => game.Title)
            .ToArray();
    }

    private static string ResolveGamesFolder()
    {
        var configuredFolder = Environment.GetEnvironmentVariable("PIXELDECK_GAMES_FOLDER");
        if (!string.IsNullOrWhiteSpace(configuredFolder))
        {
            return Path.GetFullPath(configuredFolder);
        }

        foreach (var startingPoint in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(startingPoint);
            while (directory is not null)
            {
                foreach (var folderName in new[] { "Games", "games" })
                {
                    var candidate = Path.Combine(directory.FullName, folderName);
                    if (Directory.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                directory = directory.Parent;
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "Games");
    }

    private static string CleanTitle(string fileNameWithoutExtension)
    {
        var title = fileNameWithoutExtension.Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(title) ? "Untitled Game" : title;
    }

    /// <summary>
    /// Chooses the platform for a file, letting the GameCube folder claim the
    /// disc containers whose extension alone does not identify a console.
    /// </summary>
    private static bool TryResolvePlatform(
        string extension,
        string relativeGamePath,
        out PlatformDefinition platform)
    {
        var gameCubePrefix = GameCubeFolderName + Path.DirectorySeparatorChar;
        if (GameCubeDiscExtensions.Contains(extension) &&
            relativeGamePath.StartsWith(gameCubePrefix, StringComparison.OrdinalIgnoreCase))
        {
            platform = GameCubePlatform;
            return true;
        }

        return Platforms.TryGetValue(extension, out platform!);
    }

    private static Compatibility InspectCompatibility(
        string filePath,
        string extension,
        string platformCode)
    {
        if (string.Equals(platformCode, "GC", StringComparison.Ordinal))
        {
            var disc = GameCubeDisc.Inspect(filePath, PixelCubeDiagnostics.Log);
            return new Compatibility(
                null,
                0,
                // A readable disc is offered for launch even though nothing
                // executes yet: launching is how a trace gets produced, and a
                // disc that cannot be started cannot be investigated. An
                // unreadable container stays unlaunchable, because there would
                // be nothing to trace.
                disc.IsReadable,
                disc.CompatibilityMessage,
                disc.IsReadable ? $"{disc.GameId} / {disc.RegionText}" : disc.ContainerName,
                disc.IsReadable,
                disc.IsReadable ? disc.Title : null,
                ".gci");
        }


        if (string.Equals(extension, ".z64", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".v64", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".n64", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var cartridge = N64Cartridge.Inspect(filePath);
                return new Compatibility(
                    null,
                    0,
                    // Any structurally valid cartridge is offered for launch;
                    // compatibility is reported per title rather than gating.
                    true,
                    cartridge.CompatibilityMessage,
                    cartridge.Cic == N64Cic.Unknown
                        ? "UNKNOWN CIC"
                        : cartridge.Cic.ToString().Replace("Cic", "CIC "),
                    true,
                    cartridge.Title,
                    cartridge.SaveExtension);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new Compatibility(
                    null,
                    0,
                    false,
                    "This file does not contain a recognized Nintendo 64 cartridge image.",
                    "UNKNOWN");
            }
        }

        if (string.Equals(extension, ".sfc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".smc", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var cartridge = SnesCartridge.Inspect(filePath);
                var mapText = cartridge.MapMode switch
                {
                    SnesMapMode.LoRom => "LOROM",
                    SnesMapMode.HiRom => "HIROM",
                    SnesMapMode.ExHiRom => "EXHIROM",
                    _ => "UNKNOWN"
                };
                return new Compatibility(
                    null,
                    0,
                    cartridge.IsSupported,
                    cartridge.CompatibilityMessage,
                    mapText,
                    cartridge.IsLimitedCompatibility,
                    cartridge.Title);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new Compatibility(null, 0, false, "This file does not contain a recognized standard SNES cartridge image.", "UNKNOWN");
            }
        }

        if (!string.Equals(extension, ".nes", StringComparison.OrdinalIgnoreCase))
        {
            return new Compatibility(null, 0, false, "This system does not have a PixelDeck emulator core yet.", null);
        }

        try
        {
            var cartridge = Cartridge.Inspect(filePath);
            if (!Cartridge.IsMapperSupported(cartridge.MapperNumber, cartridge.SubmapperNumber))
            {
                return new Compatibility(
                    cartridge.MapperNumber,
                    cartridge.SubmapperNumber,
                    false,
                    cartridge.SubmapperNumber == 0
                        ? $"NES mapper {cartridge.MapperNumber} is not implemented yet."
                        : $"NES mapper {cartridge.MapperNumber} submapper {cartridge.SubmapperNumber} is not implemented yet.",
                    null,
                    false);
            }

            return new Compatibility(
                cartridge.MapperNumber,
                cartridge.SubmapperNumber,
                cartridge.IsSupported,
                cartridge.CompatibilityWarning ?? "Compatible with the current local NTSC NES core.",
                null,
                cartridge.IsLimitedCompatibility);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new Compatibility(null, 0, false, "This file does not contain a valid iNES cartridge image.", null);
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var size = (double)bytes;
        var unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.#} {units[unit]}";
    }

    private static string GetCacheKey(string relativeGamePath)
    {
        var normalizedPath = relativeGamePath.Replace('\\', '/').ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    private string GetCachePath(string category, string key, string extension) =>
        Path.Combine(GamesFolder, ".pixeldeck", category, $"{key}{extension}");

    /// <summary>
    /// Picks the picture shown on a game's library card, most deliberate
    /// choice first: an image the player dropped next to the ROM, then one
    /// they captured from the pause menu, and only failing both the automatic
    /// boot screenshot.
    /// </summary>
    private static string? FindScreenshot(
        FileInfo gameFile,
        string libraryImagePath,
        string screenshotCachePath)
    {
        if (FindSidecarImage(gameFile) is { } sidecar)
        {
            return sidecar;
        }

        if (libraryImagePath.Length > 0 && File.Exists(libraryImagePath))
        {
            return libraryImagePath;
        }

        return File.Exists(screenshotCachePath) ? screenshotCachePath : null;
    }

    private static string? FindSidecarImage(FileInfo gameFile)
    {
        var basePath = Path.Combine(
            gameFile.DirectoryName!,
            Path.GetFileNameWithoutExtension(gameFile.Name));
        return ScreenshotExtensions
            .Select(extension => basePath + extension)
            .FirstOrDefault(File.Exists);
    }

    private static bool HasSidecarImage(FileInfo gameFile) => FindSidecarImage(gameFile) is not null;

    private sealed record PlatformDefinition(
        string Name,
        string Code,
        Color AccentColor,
        string? SaveFolderName = null);

    private sealed record Compatibility(
        int? MapperNumber,
        int SubmapperNumber,
        bool IsSupported,
        string Message,
        string? CartridgeDescription,
        bool IsLimited = false,
        string? CartridgeTitle = null,
        string BatterySaveExtension = ".sav");
}
