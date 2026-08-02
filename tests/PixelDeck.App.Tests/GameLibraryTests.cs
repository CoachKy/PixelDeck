using System.Security.Cryptography;
using PixelDeck.App.Services;
using PixelDeck.Emulation.N64;

namespace PixelDeck.App.Tests;

public sealed class GameLibraryTests
{
    [Fact]
    public async Task ScanAsync_FindsSupportedGamesRecursivelyAndIgnoresOtherFiles()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var nestedFolder = Directory.CreateDirectory(Path.Combine(testRoot, GameLibrary.NintendoFolderName));
            await File.WriteAllBytesAsync(Path.Combine(nestedFolder.FullName, "My_Homebrew.nes"), CreateNesImage(mapper: 0));
            await File.WriteAllTextAsync(Path.Combine(testRoot, "notes.txt"), "not a game");

            var games = await new GameLibrary(testRoot).ScanAsync();

            var game = Assert.Single(games);
            Assert.Equal("My Homebrew", game.Title);
            Assert.Equal("Nintendo Entertainment System", game.Platform);
            Assert.Equal(Path.Combine(GameLibrary.NintendoFolderName, "My_Homebrew.nes"), game.RelativePath);
            Assert.Equal("16 KB", game.SizeText);
            Assert.EndsWith(".png", game.ScreenshotCachePath);
            Assert.EndsWith(".sav", game.SaveRamPath);
            Assert.Equal(
                Path.Combine(
                    Directory.GetParent(testRoot)!.FullName,
                    "Saves",
                    GameLibrary.NintendoFolderName,
                    "My_Homebrew.sav"),
                game.SaveRamPath);
            Assert.Equal(
                Path.Combine(
                    Directory.GetParent(testRoot)!.FullName,
                    "Saves",
                    GameLibrary.NintendoFolderName,
                    "My_Homebrew.state"),
                game.SaveStatePath);
            Assert.False(game.HasScreenshot);
            Assert.Equal(0, game.MapperNumber);
            Assert.True(game.CanLaunch);
            Assert.Equal("READY", game.LaunchBadgeText);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_ReportsAnUnsupportedMapperWithoutOfferingLaunch()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(testRoot, "Future.nes"), CreateNesImage(mapper: 99));

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal(99, game.MapperNumber);
            Assert.False(game.CanLaunch);
            Assert.Contains("mapper 99", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_MarksMulticartsWithUnsupportedPeripheralContentAsPartial()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(testRoot, "Controller-and-Zapper.nes"),
                CreateNesImage(mapper: 0, nes20: true, defaultInputDevice: 0x2A));

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.True(game.CanLaunch);
            Assert.True(game.IsLimitedCompatibility);
            Assert.Equal("PARTIAL", game.LaunchBadgeText);
            Assert.Contains("Zapper", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_DoesNotOfferPalOnlyNesImagesToTheNtscCore()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(testRoot, "PAL-Homebrew.nes"),
                CreateNesImage(mapper: 0, nes20: true, timingMode: 1));

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.False(game.CanLaunch);
            Assert.Equal("UNSUPPORTED", game.LaunchBadgeText);
            Assert.Contains("NTSC-only", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_OffersStandardSnesImagesAsEarlyCoreGames()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var snesFolder = Directory.CreateDirectory(Path.Combine(testRoot, GameLibrary.SuperNintendoFolderName));
            await File.WriteAllBytesAsync(Path.Combine(snesFolder.FullName, "Homebrew.sfc"), CreateSnesImage());

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal("PIXELDECK HOMEBREW", game.Title);
            Assert.Equal("Super Nintendo Entertainment System", game.Platform);
            Assert.Equal("LOROM", game.MapperText);
            Assert.Equal("READY", game.LaunchBadgeText);
            Assert.True(game.CanLaunch);
            Assert.Contains("S-DSP stereo audio are active", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_UsesTheSharedGalleryContractForThePixel64Target()
    {
        var localTarget = FindLocalN64Target();
        if (localTarget is null)
        {
            return;
        }

        var testRoot = CreateTestDirectory();
        try
        {
            var n64Folder = Directory.CreateDirectory(
                Path.Combine(testRoot, GameLibrary.Nintendo64FolderName));
            var targetPath = Path.Combine(n64Folder.FullName, "target.z64");
            File.Copy(localTarget, targetPath);

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal("SUPER MARIO 64", game.Title);
            Assert.Equal("Nintendo 64", game.Platform);
            Assert.Equal("N64", game.PlatformCode);
            Assert.Equal("CIC 6102", game.MapperText);
            Assert.Equal("PARTIAL", game.LaunchBadgeText);
            Assert.True(game.CanLaunch);
            Assert.Contains("verified route", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("development", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".eep", game.SaveRamPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_AllowsAnUnverifiedNintendo64CartridgeToAttemptLaunch()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var n64Folder = Directory.CreateDirectory(
                Path.Combine(testRoot, GameLibrary.Nintendo64FolderName));
            await File.WriteAllBytesAsync(
                Path.Combine(n64Folder.FullName, "unverified.z64"),
                CreateN64Image("PIXEL64 UNVERIFIED"));

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal("PIXEL64 UNVERIFIED", game.Title);
            Assert.Equal("Nintendo 64", game.Platform);
            Assert.Equal("UNKNOWN CIC", game.MapperText);
            Assert.Equal("PARTIAL", game.LaunchBadgeText);
            Assert.True(game.CanLaunch);
            Assert.Contains("boot attempt enabled", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("development", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".eep", game.SaveRamPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_OffersAReadableGameCubeDiscForLaunchSoItCanBeTraced()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var gameCubeFolder = Directory.CreateDirectory(
                Path.Combine(testRoot, GameLibrary.GameCubeFolderName));
            await File.WriteAllBytesAsync(
                Path.Combine(gameCubeFolder.FullName, "Test Disc.ciso"),
                GameCubeTestSupport.CreateCompressedImage(GameCubeTestSupport.CreateDiscImage()));

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal("Nintendo GameCube", game.Platform);
            Assert.Equal("GC", game.PlatformCode);
            Assert.Equal("GTSE01 / NTSC-U", game.MapperText);

            // Launching is how a trace is produced, so a readable disc starts
            // even though nothing executes behind it.
            Assert.True(game.CanLaunch);
            Assert.Equal("PARTIAL", game.LaunchBadgeText);
            Assert.Contains("trace log", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".gci", game.SaveRamPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                Path.Combine(
                    Directory.GetParent(testRoot)!.FullName,
                    "Saves",
                    GameLibrary.GameCubeFolderName,
                    "Test Disc.gci"),
                game.SaveRamPath);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_RefusesToLaunchAGameCubeDiscItCannotRead()
    {
        var testRoot = CreateTestDirectory();
        try
        {
            var gameCubeFolder = Directory.CreateDirectory(
                Path.Combine(testRoot, GameLibrary.GameCubeFolderName));
            await File.WriteAllBytesAsync(
                Path.Combine(gameCubeFolder.FullName, "Broken.iso"),
                new byte[0x1000]);

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal("GC", game.PlatformCode);
            Assert.False(game.CanLaunch);
            Assert.Equal("UNSUPPORTED", game.LaunchBadgeText);
            Assert.Contains("magic word", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_TreatsAnIsoAsAGameCubeDiscOnlyInsideTheGameCubeFolder()
    {
        // ".iso" is the ordinary extension for a GameCube disc and for a disc
        // image of anything else, so the folder the player chose is the only
        // evidence of which one this is.
        var testRoot = CreateTestDirectory();
        try
        {
            var image = GameCubeTestSupport.CreateDiscImage();
            var gameCubeFolder = Directory.CreateDirectory(
                Path.Combine(testRoot, GameLibrary.GameCubeFolderName));
            await File.WriteAllBytesAsync(
                Path.Combine(gameCubeFolder.FullName, "Filed.iso"),
                image);
            await File.WriteAllBytesAsync(Path.Combine(testRoot, "Loose.iso"), image);

            var games = await new GameLibrary(testRoot).ScanAsync();

            var filed = Assert.Single(games, game => game.FileName == "Filed.iso");
            var loose = Assert.Single(games, game => game.FileName == "Loose.iso");

            Assert.Equal("GC", filed.PlatformCode);
            Assert.Equal("DISC", loose.PlatformCode);
            Assert.Empty(loose.SaveRamPath);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_UsesAValidatedNintendoHeaderTitleForNesImages()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var image = CreateNesImage(mapper: 0);
            WriteNintendoHeaderTitle(image, "PIXEL ADVENTURE");
            await File.WriteAllBytesAsync(Path.Combine(testRoot, "A1B2C3D4.nes"), image);

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal("PIXEL ADVENTURE", game.Title);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_DoesNotLetALegacyNintendoHeaderReplaceAReadableFilename()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var image = CreateNesImage(mapper: 0);
            WriteNintendoHeaderTitle(image, "ABBREVIATED");
            await File.WriteAllBytesAsync(Path.Combine(testRoot, "Readable Game Name.nes"), image);

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal("Readable Game Name", game.Title);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_UsesAnOfflineDatTitleAndInvalidatesTheFilenameCache()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var image = CreateNesImage(mapper: 0);
            var gamePath = Path.Combine(testRoot, "not-the-real-title.nes");
            await File.WriteAllBytesAsync(gamePath, image);
            var library = new GameLibrary(testRoot);

            var filenameGame = Assert.Single(await library.ScanAsync());
            Assert.Equal("not-the-real-title", filenameGame.Title);

            var payloadSha1 = Convert.ToHexString(SHA1.HashData(image.AsSpan(16)));
            await File.WriteAllTextAsync(
                Path.Combine(library.MetadataFolder, "Nintendo - Nintendo Entertainment System.dat"),
                $$"""
                clrmamepro (
                    name "Nintendo - Nintendo Entertainment System"
                )
                game (
                    name "Catalog Title (USA)"
                    description "Catalog Title (USA)"
                    rom (
                        name "Catalog Title (USA).nes"
                        size {{image.Length - 16}}
                        sha1 {{payloadSha1}}
                    )
                )
                """);

            var catalogGame = Assert.Single(await library.ScanAsync());

            Assert.Equal("Catalog Title (USA)", catalogGame.Title);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Theory]
    [InlineData(".xml")]
    [InlineData(".json")]
    public async Task ScanAsync_AcceptsXmlAndJsonOfflineCatalogs(string catalogExtension)
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var image = CreateNesImage(mapper: 0);
            await File.WriteAllBytesAsync(Path.Combine(testRoot, "unknown.nes"), image);
            var library = new GameLibrary(testRoot);
            var sha1 = Convert.ToHexString(SHA1.HashData(image));
            var catalog = catalogExtension == ".xml"
                ? $$"""
                    <datafile>
                      <game name="Structured Catalog Title">
                        <description>Structured Catalog Title</description>
                        <rom name="game.nes" size="{{image.Length}}" sha1="{{sha1}}" />
                      </game>
                    </datafile>
                    """
                : $$"""
                    {
                      "games": [
                        {
                          "title": "Structured Catalog Title",
                          "sha1": "{{sha1}}"
                        }
                      ]
                    }
                    """;
            await File.WriteAllTextAsync(
                Path.Combine(library.MetadataFolder, "catalog" + catalogExtension),
                catalog);

            var game = Assert.Single(await library.ScanAsync());

            Assert.Equal("Structured Catalog Title", game.Title);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_OffersDsp1SnesImagesWithTheCorrectCapability()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var snesFolder = Directory.CreateDirectory(
                Path.Combine(testRoot, GameLibrary.SuperNintendoFolderName));
            await File.WriteAllBytesAsync(
                Path.Combine(snesFolder.FullName, "Kart.sfc"),
                CreateDsp1SnesImage());

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal("HIROM", game.MapperText);
            Assert.Equal("READY", game.LaunchBadgeText);
            Assert.True(game.CanLaunch);
            Assert.Contains("DSP-1", game.CompatibilityText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_LoadsASameNamedLocalScreenshot()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var gamePath = Path.Combine(testRoot, "Preview.nes");
            var screenshotPath = Path.Combine(testRoot, "Preview.png");
            var onePixelPng = Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

            await File.WriteAllBytesAsync(gamePath, new byte[16]);
            await File.WriteAllBytesAsync(screenshotPath, onePixelPng);

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());

            Assert.Equal(screenshotPath, game.ScreenshotPath);
            Assert.False(game.HasScreenshot);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_ReturnsAnEmptyLibraryWhenNoGamesExist()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var games = await new GameLibrary(testRoot).ScanAsync();

            Assert.Empty(games);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public void ConstructorCreatesConsoleLibraryFolders()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var library = new GameLibrary(testRoot);

            Assert.Equal(Path.Combine(testRoot, GameLibrary.NintendoFolderName), library.NintendoFolder);
            Assert.Equal(Path.Combine(testRoot, GameLibrary.Nintendo64FolderName), library.Nintendo64Folder);
            Assert.Equal(Path.Combine(testRoot, GameLibrary.SuperNintendoFolderName), library.SuperNintendoFolder);
            Assert.True(Directory.Exists(library.NintendoFolder));
            Assert.True(Directory.Exists(library.Nintendo64Folder));
            Assert.True(Directory.Exists(library.SuperNintendoFolder));
            Assert.Equal(
                Path.Combine(Directory.GetParent(testRoot)!.FullName, "Saves"),
                library.SavesFolder);
            Assert.True(Directory.Exists(library.NintendoSavesFolder));
            Assert.True(Directory.Exists(library.Nintendo64SavesFolder));
            Assert.True(Directory.Exists(library.SuperNintendoSavesFolder));
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_PreservesNestedConsoleFoldersInTheSaveLayout()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var nestedFolder = Directory.CreateDirectory(
                Path.Combine(testRoot, GameLibrary.SuperNintendoFolderName, "RPG"));
            await File.WriteAllBytesAsync(
                Path.Combine(nestedFolder.FullName, "Final Fantasy III.sfc"),
                CreateSnesImage());

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());
            var expectedFolder = Path.Combine(
                Directory.GetParent(testRoot)!.FullName,
                "Saves",
                GameLibrary.SuperNintendoFolderName,
                "RPG");

            Assert.Equal(Path.Combine(expectedFolder, "Final Fantasy III.sav"), game.SaveRamPath);
            Assert.Equal(Path.Combine(expectedFolder, "Final Fantasy III.state"), game.SaveStatePath);
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task ScanAsync_MigratesLegacyBatteryAndStateFilesWithoutOverwritingNewSaves()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            var relativeGamePath = Path.Combine(
                GameLibrary.NintendoFolderName,
                "Legacy Game.nes");
            var gamePath = Path.Combine(testRoot, relativeGamePath);
            Directory.CreateDirectory(Path.GetDirectoryName(gamePath)!);
            await File.WriteAllBytesAsync(gamePath, CreateNesImage(mapper: 0));

            var legacyKey = GetLegacyCacheKey(relativeGamePath);
            var legacyBatteryFolder = Directory.CreateDirectory(
                Path.Combine(testRoot, ".pixeldeck", "saves"));
            var legacyStateFolder = Directory.CreateDirectory(
                Path.Combine(testRoot, ".pixeldeck", "screenshots"));
            var legacyBatteryPath = Path.Combine(legacyBatteryFolder.FullName, legacyKey + ".sav");
            var legacyStatePath = Path.Combine(legacyStateFolder.FullName, legacyKey + ".slot-002.state");
            await File.WriteAllBytesAsync(legacyBatteryPath, [0x10, 0x20]);
            await File.WriteAllBytesAsync(legacyStatePath, [0x30, 0x40]);

            var game = Assert.Single(await new GameLibrary(testRoot).ScanAsync());
            var migratedStatePath = Path.Combine(
                Path.GetDirectoryName(game.SaveStatePath)!,
                "Legacy Game.slot-002.state");

            Assert.Equal([0x10, 0x20], await File.ReadAllBytesAsync(game.SaveRamPath));
            Assert.Equal([0x30, 0x40], await File.ReadAllBytesAsync(migratedStatePath));
            Assert.False(File.Exists(legacyBatteryPath));
            Assert.False(File.Exists(legacyStatePath));

            await File.WriteAllBytesAsync(game.SaveRamPath, [0xAA]);
            await File.WriteAllBytesAsync(legacyBatteryPath, [0xBB]);
            _ = await new GameLibrary(testRoot).ScanAsync();

            Assert.Equal([0xAA], await File.ReadAllBytesAsync(game.SaveRamPath));
            Assert.Equal([0xBB], await File.ReadAllBytesAsync(legacyBatteryPath));
        }
        finally
        {
            DeleteTestDirectory(testRoot);
        }
    }

    private static string CreateTestDirectory()
    {
        var testParent = Path.Combine(Path.GetTempPath(), "PixelDeck.Tests");
        var testContainer = Path.Combine(testParent, Guid.NewGuid().ToString("N"));
        return Directory.CreateDirectory(Path.Combine(testContainer, "Games")).FullName;
    }

    private static string? FindLocalN64Target()
    {
        var folder = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "Games",
            GameLibrary.Nintendo64FolderName));
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(path =>
                    Path.GetExtension(path) is ".z64" or ".v64" or ".n64")
                .FirstOrDefault(path =>
                {
                    try
                    {
                        return N64Cartridge.Inspect(path).IsSuperMario64UsRevision0;
                    }
                    catch (InvalidDataException)
                    {
                        return false;
                    }
                })
            : null;
    }

    private static byte[] CreateNesImage(
        int mapper,
        bool nes20 = false,
        byte defaultInputDevice = 0,
        byte timingMode = 0)
    {
        var image = new byte[16 + 16_384];
        image[0] = (byte)'N';
        image[1] = (byte)'E';
        image[2] = (byte)'S';
        image[3] = 0x1A;
        image[4] = 1;
        image[6] = (byte)((mapper & 0x0F) << 4);
        image[7] = (byte)((mapper & 0xF0) | (nes20 ? 0x08 : 0));
        if (nes20)
        {
            image[8] = (byte)((mapper >> 8) & 0x0F);
            image[12] = timingMode;
            image[15] = defaultInputDevice;
        }

        return image;
    }

    private static byte[] CreateN64Image(string title)
    {
        var image = new byte[0x2000];
        image[0] = 0x80;
        image[1] = 0x37;
        image[2] = 0x12;
        image[3] = 0x40;
        image[0x08] = 0x80;
        image[0x0A] = 0x04;
        System.Text.Encoding.ASCII.GetBytes(title.PadRight(20))
            .AsSpan(0, 20)
            .CopyTo(image.AsSpan(0x20, 20));
        image[0x3B] = (byte)'N';
        image[0x3C] = (byte)'P';
        image[0x3D] = (byte)'X';
        image[0x3E] = (byte)'E';
        return image;
    }

    private static byte[] CreateSnesImage()
    {
        var image = new byte[32 * 1024];
        const int header = 0x7FC0;
        "PIXELDECK HOMEBREW  ".Select(character => (byte)character).ToArray().CopyTo(image, header);
        image[header + 0x15] = 0x20;
        image[header + 0x16] = 0x00;
        image[header + 0x17] = 0x05;
        image[header + 0x19] = 0x01;
        image[header + 0x1C] = 0xCB;
        image[header + 0x1D] = 0xED;
        image[header + 0x1E] = 0x34;
        image[header + 0x1F] = 0x12;
        image[header + 0x3C] = 0x00;
        image[header + 0x3D] = 0x80;
        return image;
    }

    private static byte[] CreateDsp1SnesImage()
    {
        var image = new byte[64 * 1024];
        const int header = 0xFFC0;
        "PIXELDECK DSP1 TEST  ".Select(character => (byte)character).ToArray().CopyTo(image, header);
        image[header + 0x15] = 0x31;
        image[header + 0x16] = 0x05;
        image[header + 0x17] = 0x06;
        image[header + 0x18] = 0x03;
        image[header + 0x19] = 0x01;
        image[header + 0x1C] = 0xCB;
        image[header + 0x1D] = 0xED;
        image[header + 0x1E] = 0x34;
        image[header + 0x1F] = 0x12;
        image[header + 0x3C] = 0x00;
        image[header + 0x3D] = 0x80;
        return image;
    }

    private static void WriteNintendoHeaderTitle(byte[] image, string title)
    {
        var titleBytes = System.Text.Encoding.ASCII.GetBytes(title);
        Assert.InRange(titleBytes.Length, 2, 16);

        var nintendoHeader = image.AsSpan(16 + 16_384 - 32, 32);
        titleBytes.CopyTo(nintendoHeader.Slice(16 - titleBytes.Length, titleBytes.Length));
        nintendoHeader[0x16] = 1;
        nintendoHeader[0x17] = (byte)(titleBytes.Length - 1);

        var validationSum = 0;
        for (var index = 0x12; index < 0x19; index++)
        {
            validationSum = (validationSum + nintendoHeader[index]) & 0xFF;
        }

        nintendoHeader[0x19] = unchecked((byte)-validationSum);
    }

    private static void DeleteTestDirectory(string testRoot)
    {
        var testParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PixelDeck.Tests"))
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolvedRoot = Path.GetFullPath(testRoot);
        var testContainer = Directory.GetParent(resolvedRoot)?.FullName
            ?? throw new InvalidOperationException("The test games folder has no parent.");

        if (!resolvedRoot.StartsWith(testParent, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(resolvedRoot), "Games", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to remove a directory outside the PixelDeck test area.");
        }

        if (Directory.Exists(testContainer))
        {
            Directory.Delete(testContainer, recursive: true);
        }
    }

    private static string GetLegacyCacheKey(string relativeGamePath)
    {
        var normalizedPath = relativeGamePath.Replace('\\', '/').ToUpperInvariant();
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedPath));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }
}
