using System.Security.Cryptography;
using System.Text;
using Avalonia.Media;
using PixelDeck.App.Models;
using PixelDeck.Emulation.Nes;
using PixelDeck.Emulation.Snes;

namespace PixelDeck.App.Services;

public sealed class GameLibrary
{
    public const string NintendoFolderName = "Nintendo";
    public const string SuperNintendoFolderName = "SuperNintendo";

    private static readonly string[] ScreenshotExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];

    private static readonly IReadOnlyDictionary<string, PlatformDefinition> Platforms =
        new Dictionary<string, PlatformDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [".nes"] = new("Nintendo Entertainment System", "NES", Color.Parse("#F04464")),
            [".fds"] = new("Famicom Disk System", "FDS", Color.Parse("#E9573F")),
            [".sfc"] = new("Super Nintendo Entertainment System", "SNES", Color.Parse("#8B7CF6")),
            [".smc"] = new("Super Nintendo Entertainment System", "SNES", Color.Parse("#8B7CF6")),
            [".gb"] = new("Game Boy", "GB", Color.Parse("#9BBF45")),
            [".gbc"] = new("Game Boy Color", "GBC", Color.Parse("#F0A43A")),
            [".gba"] = new("Game Boy Advance", "GBA", Color.Parse("#6C63D9")),
            [".n64"] = new("Nintendo 64", "N64", Color.Parse("#38B7A5")),
            [".z64"] = new("Nintendo 64", "N64", Color.Parse("#38B7A5")),
            [".v64"] = new("Nintendo 64", "N64", Color.Parse("#38B7A5")),
            [".nds"] = new("Nintendo DS", "NDS", Color.Parse("#5A9CF8")),
            [".gcm"] = new("Nintendo GameCube", "GC", Color.Parse("#6D72E8")),
            [".rvz"] = new("Nintendo GameCube / Wii", "GC/WII", Color.Parse("#4CB4D8")),
            [".wbfs"] = new("Nintendo Wii", "WII", Color.Parse("#68C5D6")),
            [".iso"] = new("Disc Image", "DISC", Color.Parse("#5E83A8")),
            [".dol"] = new("Nintendo Homebrew", "DOL", Color.Parse("#F28B42")),
            [".elf"] = new("Homebrew", "ELF", Color.Parse("#E4B63E"))
        };

    public GameLibrary(string? gamesFolder = null)
    {
        GamesFolder = gamesFolder is null ? ResolveGamesFolder() : Path.GetFullPath(gamesFolder);
        Directory.CreateDirectory(GamesFolder);
        NintendoFolder = Directory.CreateDirectory(Path.Combine(GamesFolder, NintendoFolderName)).FullName;
        SuperNintendoFolder = Directory.CreateDirectory(Path.Combine(GamesFolder, SuperNintendoFolderName)).FullName;
    }

    public string GamesFolder { get; }

    public string NintendoFolder { get; }

    public string SuperNintendoFolder { get; }

    public Task<IReadOnlyList<GameEntry>> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<GameEntry>>(() => Scan(cancellationToken), cancellationToken);

    private IReadOnlyList<GameEntry> Scan(CancellationToken cancellationToken)
    {
        var results = new List<GameEntry>();
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
            if (!Platforms.TryGetValue(extension, out var platform))
            {
                continue;
            }

            try
            {
                var file = new FileInfo(filePath);
                var relativePath = Path.GetRelativePath(GamesFolder, file.FullName);
                var cacheKey = GetCacheKey(relativePath);
                var screenshotCachePath = GetCachePath("screenshots", cacheKey, ".png");
                var screenshotPath = FindScreenshot(file, screenshotCachePath);
                var compatibility = InspectCompatibility(file.FullName, extension);

                results.Add(new GameEntry(
                    CleanTitle(Path.GetFileNameWithoutExtension(file.Name)),
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
                    SaveRamPath = GetCachePath("saves", cacheKey, ".sav"),
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

    private static Compatibility InspectCompatibility(string filePath, string extension)
    {
        if (string.Equals(extension, ".sfc", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".smc", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var cartridge = SnesCartridge.Inspect(filePath);
                var mapText = cartridge.MapMode == SnesMapMode.LoRom ? "LOROM" : "HIROM";
                return new Compatibility(
                    null,
                    0,
                    cartridge.IsSupported,
                    cartridge.CompatibilityMessage,
                    mapText);
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

    private static string? FindScreenshot(FileInfo gameFile, string screenshotCachePath)
    {
        var basePath = Path.Combine(gameFile.DirectoryName!, Path.GetFileNameWithoutExtension(gameFile.Name));
        var sidecarScreenshot = ScreenshotExtensions
            .Select(extension => basePath + extension)
            .FirstOrDefault(File.Exists);

        return sidecarScreenshot ?? (File.Exists(screenshotCachePath) ? screenshotCachePath : null);
    }

    private sealed record PlatformDefinition(string Name, string Code, Color AccentColor);

    private sealed record Compatibility(
        int? MapperNumber,
        int SubmapperNumber,
        bool IsSupported,
        string Message,
        string? CartridgeDescription,
        bool IsLimited = false);
}
